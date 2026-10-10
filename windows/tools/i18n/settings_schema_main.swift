import Foundation
// Dumps the settings schema: every registered default with its type and value,
// the feature availability layer, and the backup include/exclude sets.
func describe(_ value: Any) -> [String: Any] {
    switch value {
    case let v as NSNumber where CFGetTypeID(v) == CFBooleanGetTypeID(): return ["type": "bool", "default": v.boolValue]
    case let v as Int: return ["type": "int", "default": v]
    case let v as Double: return ["type": "double", "default": v]
    case let v as String: return ["type": "string", "default": v]
    case let v as [String]: return ["type": "string[]", "default": v]
    case let v as [String: String]: return ["type": "map<string,string>", "default": v]
    case let v as Data: return ["type": "data", "default": v.base64EncodedString()]
    case let v as [Any]: return ["type": "array", "default": "\(v)"]
    case let v as [String: Any]: return ["type": "map", "default": "\(v)"]
    default: return ["type": "\(type(of: value))", "default": "\(value)"]
    }
}
var registered: [String: Any] = [:]
for (k, v) in Defaults.registeredDefaults { registered[k] = describe(v) }
var availability: [String: Any] = [:]
for (k, v) in AppFeature.availabilityDefaults { availability[k] = v }
let schema: [String: Any] = [
    "registeredDefaults": registered,
    "featureAvailabilityDefaults": availability,
    "backupExportKeys": SettingsBackupSupport.exportKeys().sorted(),
    "backupUnregisteredPreferenceKeys": SettingsBackupSupport.unregisteredPreferenceKeys.sorted(),
    "backupMachineStateKeys": SettingsBackupSupport.machineStateKeys.sorted(),
    "essentialPreset": FeaturePreset.essential.features.map(\.rawValue).sorted(),
]
let data = try! JSONSerialization.data(withJSONObject: schema, options: [.prettyPrinted, .sortedKeys])
FileManager.default.createFile(atPath: CommandLine.arguments[1], contents: data)
print("registered: \(registered.count), availability: \(availability.count), export: \(SettingsBackupSupport.exportKeys().count), machine-state: \(SettingsBackupSupport.machineStateKeys.count)")
