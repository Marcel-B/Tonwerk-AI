import Foundation
import OpenAPIRuntime

/// Dates as .NET writes them (`2026-10-15T12:23:12.1234567+00:00`): ISO 8601 with up to seven fractional digits,
/// which Foundation's ISO 8601 parser does not take beyond three, or none at all.
public struct TonwerkDates: DateTranscoder {
    public init() {}

    public func encode(_ date: Date) throws -> String {
        formatter(fractional: true).string(from: date)
    }

    public func decode(_ text: String) throws -> Date {
        let (normalized, fractional) = Self.milliseconds(text)
        guard let date = formatter(fractional: fractional).date(from: normalized) else {
            throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "Not an ISO 8601 date: \(text)"))
        }
        return date
    }

    /// The fraction cut or padded to three digits, and whether there is one.
    static func milliseconds(_ text: String) -> (String, Bool) {
        guard let time = text.firstIndex(of: "T"), let dot = text[time...].firstIndex(of: ".") else {
            return (text, false)
        }
        let digits = text[text.index(after: dot)...].prefix(while: \.isNumber)
        let rest = text[digits.endIndex...]
        let three = String(digits.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)
        return (String(text[..<dot]) + "." + three + String(rest), true)
    }

    // A new formatter per call: ISO8601DateFormatter is not Sendable everywhere, and dates are few per answer.
    private func formatter(fractional: Bool) -> ISO8601DateFormatter {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = fractional ? [.withInternetDateTime, .withFractionalSeconds] : [.withInternetDateTime]
        return formatter
    }
}

extension JSONDecoder {
    /// Decodes the generated types the way the generated client does, for JSON that arrives another way (events).
    public static func tonwerk() -> JSONDecoder {
        let decoder = JSONDecoder()
        let dates = TonwerkDates()
        decoder.dateDecodingStrategy = .custom { decoder in
            try dates.decode(try decoder.singleValueContainer().decode(String.self))
        }
        return decoder
    }
}
