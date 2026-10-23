#!/usr/bin/env python3
"""Decides whether main has a new Mac app release and writes its version and notes (release-mac.yml).

A release follows only when something the app is built from changed since the newest tag app-v<version>: apple/
(the app, TonwerkKit, the copied DSP) or LICENSE/NOTICE, which go into the DMG. Server, web and deploy commits
reach the Mac through main and never touch the app, so they neither release it nor fill its notes.

The version keeps its major (raised only by hand) and counts on: minor for new functions, patch for fixes and
polish. Claude reads the changes, decides which, and writes the notes users see in the update window; without
a key (ANTHROPIC_API_KEY) or when the call fails, the release is a patch and the notes are the changes' titles,
so a missing summary never holds a release back.

    app-release-plan.py SHA [--version V] [--notes TEXT]

Writes release, version, notes_md and notes_html to $GITHUB_OUTPUT (or prints them).
"""
import argparse
import html
import os
import re
import subprocess
import sys

APP_PATHS = ["apple", "LICENSE", "NOTICE"]
MODEL = "claude-opus-5-5"
SYSTEM = """You write the release notes of Tonwerk-AI, a Mac app that controls a self-hosted server for making \
songs with AI models (YuE2, ACE-Step), voices, covers, videos and Logic projects. You get the titles and \
descriptions of the changes to the app since the last release, written by its developer in German.

Decide what kind of release this is: "minor" when users get something new they can do (a new page, function, \
option or setting), "patch" when everything is a fix, a layout or wording change, speed, or internal work.

Write the notes in German for the people using the app: two to five short bullet points, each one sentence, the \
most noticeable first. Say what changed for them, not how it was built: no file, class or function names, no PR \
numbers, no build or workflow details. Leave out changes users never notice; if nothing is noticeable, write one \
point such as "Kleine Verbesserungen und Fehlerbehebungen"."""


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def version_key(tag):
    return tuple(int(part) for part in tag.removeprefix("app-v").split("."))


def last_release():
    tags = [t for t in git("tag", "-l", "app-v*").split() if re.fullmatch(r"app-v\d+\.\d+\.\d+", t)]
    return max(tags, key=version_key) if tags else None


def changes(since, sha):
    """The app's changes as (title, description), first parent only, so a merged PR counts once by its title."""
    span = f"{since}..{sha}" if since else sha
    log = git("log", "--first-parent", "--format=%s%x1f%b%x1e", span, "--", *APP_PATHS)
    result = []
    for entry in filter(str.strip, log.split("\x1e")):
        title, _, body = entry.strip().partition("\x1f")
        # A merge commit's subject is "Merge pull request #n from …"; the PR's title is the body's first line.
        if title.startswith("Merge pull request") and body.strip():
            title, _, body = body.strip().partition("\n")
        body = "\n".join(line for line in body.splitlines()
                         if not re.match(r"(Co-[Aa]uthored-[Bb]y|Claude-Session|https://claude\.ai/)", line))
        result.append((re.sub(r"\s*\(#\d+\)$", "", title.strip()), body.strip()))
    return result


def summarize(entries):
    """Asks Claude for the kind of release and the notes; None when it cannot."""
    if not os.environ.get("ANTHROPIC_API_KEY"):
        print("No ANTHROPIC_API_KEY: patch release, notes from the titles.", file=sys.stderr)
        return None
    try:
        from typing import List, Literal

        import anthropic
        from pydantic import BaseModel

        class Plan(BaseModel):
            kind: Literal["minor", "patch"]
            notes: List[str]

        text = "\n\n".join(f"## {title}\n{body[:3000]}" for title, body in entries)
        response = anthropic.Anthropic().beta.messages.parse(
            model=MODEL,
            max_tokens=16000,
            system=SYSTEM,
            output_config={"effort": "low"},
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
            messages=[{"role": "user", "content": text[:200000]}],
            output_format=Plan,
        )
        if response.stop_reason != "end_turn" or response.parsed_output is None:
            print(f"Claude stopped with {response.stop_reason}; falling back.", file=sys.stderr)
            return None
        plan = response.parsed_output
        notes = [n.strip() for n in plan.notes if n.strip()][:5]
        return (plan.kind, notes) if notes else None
    except Exception as error:  # a failed summary must not stop the release
        print(f"Summary failed ({type(error).__name__}: {error}); falling back.", file=sys.stderr)
        return None


def next_version(last, kind):
    if not last:
        return "1.0.0"
    major, minor, patch = version_key(last)
    return f"{major}.{minor + 1}.0" if kind == "minor" else f"{major}.{minor}.{patch + 1}"


def output(name, value):
    path = os.environ.get("GITHUB_OUTPUT")
    if not path:
        print(f"--- {name}\n{value}")
        return
    with open(path, "a", encoding="utf-8") as out:
        out.write(f"{name}<<PLAN_EOF\n{value}\nPLAN_EOF\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("sha")
    parser.add_argument("--version", default="")
    parser.add_argument("--notes", default="")
    args = parser.parse_args()

    last = last_release()
    entries = changes(last, args.sha)
    print(f"Last release {last}, {len(entries)} app change(s) since:", file=sys.stderr)
    for title, _ in entries:
        print(f"  {title}", file=sys.stderr)

    # By hand with a version, a release always follows (also to repeat one); automatically, only with changes.
    if not entries and not args.version:
        output("release", "false")
        return

    summary = None if args.notes else summarize(entries) if entries else None
    if args.notes:
        notes = [args.notes.strip()]
    elif summary:
        notes = summary[1]
    else:
        notes = [title for title, _ in entries][:8] or ["Kleine Verbesserungen und Fehlerbehebungen"]
    version = args.version or next_version(last, summary[0] if summary else "patch")

    output("release", "true")
    output("version", version)
    output("notes_md", "\n".join(f"- {note}" for note in notes))
    output("notes_html", "<ul>" + "".join(f"<li>{html.escape(note)}</li>" for note in notes) + "</ul>")


if __name__ == "__main__":
    main()
