// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>One setting after the overlay: the value in force, and why it
/// differs from the user's own — null when it doesn't.</summary>
public readonly record struct ResolvedSetting<T>(T Value, string? Reason)
{
    public bool IsOverridden => Reason is not null;
}

/// <summary>
/// Resolves what a broadcast setting actually amounts to, by laying the role's
/// coercions and any minimum in force over what the user configured.
/// </summary>
/// <remarks>
/// Firmware rewrites its stored config when a role is installed, so choosing
/// ROUTER for an afternoon destroys the intervals you had and leaves you to
/// remember them. Resolving on read instead means the user's numbers are never
/// touched: changing role and back restores them, and nothing has to be typed
/// again. The price is that a settings box no longer says what goes on the air,
/// which is what <see cref="ResolvedSetting{T}.Reason"/> is for — the UI shows
/// it beside any control the overlay has overruled.
/// </remarks>
public static class SettingOverlay
{
    /// <summary>
    /// An interval: the role's value if it declares one, then raised to any
    /// minimum in force. Reports whichever of the two actually moved it, so the
    /// note names the rule the user would have to change to get their number
    /// back.
    /// </summary>
    public static ResolvedSetting<int> Interval(int userSeconds, int? roleSeconds, string? roleName,
                                                int floorSeconds, string floorReason)
    {
        int value = roleSeconds ?? userSeconds;
        string? reason = value != userSeconds ? RoleReason(roleName) : null;

        if (floorSeconds > value)
        {
            value = floorSeconds;
            reason = floorReason;
        }
        // A role or floor that lands on the user's own number has overruled
        // nothing worth saying.
        return new ResolvedSetting<int>(value, value == userSeconds ? null : reason);
    }

    public static ResolvedSetting<bool> Flag(bool userValue, bool? roleValue, string? roleName) =>
        roleValue is bool r && r != userValue
            ? new ResolvedSetting<bool>(r, RoleReason(roleName))
            : new ResolvedSetting<bool>(userValue, null);

    public static ResolvedSetting<uint> Distance(uint userMeters, uint? roleMeters, string? roleName) =>
        roleMeters is uint m && m != userMeters
            ? new ResolvedSetting<uint>(m, RoleReason(roleName))
            : new ResolvedSetting<uint>(userMeters, null);

    private static string RoleReason(string? roleName) =>
        string.IsNullOrWhiteSpace(roleName) ? "role" : $"role {roleName.Trim()}";

    /// <summary>
    /// Whole units where the number divides evenly, which every role default
    /// and minimum does. A user's own odd number never reaches this — it is
    /// only formatted when something has overruled it.
    /// </summary>
    public static string Duration(int seconds) => seconds switch
    {
        <= 0 => "0 s",
        _ when seconds % 86400 == 0 => $"{seconds / 86400} d",
        _ when seconds % 3600 == 0 => $"{seconds / 3600} h",
        _ when seconds % 60 == 0 => $"{seconds / 60} min",
        _ => $"{seconds} s",
    };
}
