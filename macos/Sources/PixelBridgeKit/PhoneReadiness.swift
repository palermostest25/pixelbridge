import Foundation
public enum PhoneReadiness: Equatable, Sendable { case booting, firstUnlock, ready }
public enum PhoneBoot {
    public static func evaluate(completed: String, userState: String) -> PhoneReadiness {
        guard completed.trimmingCharacters(in: .whitespacesAndNewlines) == "1" else { return .booting }
        let state = userState.trimmingCharacters(in: .whitespacesAndNewlines)
        return ["BOOTING", "RUNNING_LOCKED", "RUNNING_UNLOCKING"].contains(state) ? .firstUnlock : .ready
    }
}
