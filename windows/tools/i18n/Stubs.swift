import AppKit
// Minimal stand-ins for three app-only types that a few support files mention.
// They play no part in any string catalog; the real ones live in the
// AppKit window host and NotchService.
final class NotchPanel: NSPanel {}
final class NotchActivationButton: NSButton {}
enum NotchNotice { static let minimumWing: CGFloat = 36 }
