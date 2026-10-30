#!/usr/bin/env python3
"""The app's texts against its string catalog.

The compiler writes every text the app looks up (SwiftUI's LocalizedStringKey, String(localized:)) into .stringsdata
files when asked with -emit-localized-strings; the screens live in the TonwerkUI package, which Xcode does not extract
into the app's catalog, so CI does it and this script holds the catalog to it.

  localizations.py check <stringsdata dir> <Localizable.xcstrings>   fails when a text has no English translation
  localizations.py dump <stringsdata dir>                             every key with its first place, as JSON
"""
import json
import os
import sys


def extracted(folder):
    keys = {}
    for root, _, files in os.walk(folder):
        for name in files:
            if not name.endswith(".stringsdata"):
                continue
            with open(os.path.join(root, name), encoding="utf-8") as f:
                data = json.load(f)
            source = os.path.basename(data.get("source", name))
            for table, entries in data.get("tables", {}).items():
                if table != "Localizable":
                    continue
                for entry in entries:
                    line = entry.get("location", {}).get("startingLine", 0)
                    keys.setdefault(entry["key"], f"{source}:{line}")
    return keys


def translated(catalog_path):
    with open(catalog_path, encoding="utf-8") as f:
        catalog = json.load(f)
    result = set()
    for key, entry in catalog.get("strings", {}).items():
        english = entry.get("localizations", {}).get("en", {})
        if english.get("stringUnit", {}).get("value") is not None or "variations" in english:
            result.add(key)
        elif entry.get("shouldTranslate") is False:
            result.add(key)
    return result


def main(argv):
    if len(argv) == 3 and argv[1] == "dump":
        json.dump(extracted(argv[2]), sys.stdout, ensure_ascii=False, indent=1, sort_keys=True)
        print()
        return 0
    if len(argv) == 4 and argv[1] == "check":
        keys = extracted(argv[2])
        if not keys:
            print("No .stringsdata found: was the build asked to emit localized strings?")
            return 1
        known = translated(argv[3])
        missing = sorted(k for k in keys if k not in known)
        print(f"{len(keys)} texts in the app, {len(known)} in the catalog with English.")
        for key in missing:
            print(f"::error file=apple/TonwerkKit/Sources/TonwerkUI/{keys[key].split(':')[0]},"
                  f"line={keys[key].split(':')[1]}::No English translation: {json.dumps(key, ensure_ascii=False)}")
        unused = sum(1 for k in known if k not in keys)
        # Texts looked up at run time (String.LocalizationValue of a variable) and the iOS-only ones are among these.
        print(f"{unused} catalog entries the compiler did not see (looked up at run time, iOS only, or no longer used).")
        return 1 if missing else 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
