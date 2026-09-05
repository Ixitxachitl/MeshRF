// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Location;

/// <summary>
/// Which of Position's two altitude fields a reading belongs in, and what
/// number goes there.
/// </summary>
/// <remarks>
/// Firmware's ALTITUDE_MSL position flag chooses between <c>altitude</c>
/// (height above mean sea level) and <c>altitude_hae</c> (height above the
/// WGS-84 ellipsoid), and the TAK roles clear it because a CoT carries HAE. A
/// receiver reports both, so for firmware the flag really is only a choice.
///
/// Everything MeshRF holds is orthometric — NMEA's GGA reports height above
/// the geoid, and so does the map anyone reads a home altitude off. Moving to
/// the other field is therefore a conversion and not a label: the two differ by
/// the geoid separation, which is tens of metres almost everywhere and reaches
/// a hundred. Writing an MSL number into <c>altitude_hae</c> would put a
/// wrong number on the air under a name that says it is right.
///
/// The separation is the one thing that makes the conversion exact, and GGA
/// carries it two fields along from the altitude. Without one — a hand-typed
/// altitude, a receiver that omits the field — the honest answer is the datum
/// we can prove, so the reading stays MSL and says so.
/// </remarks>
public static class AltitudeDatum
{
    /// <summary>
    /// The altitude to transmit and whether it is above mean sea level.
    /// </summary>
    /// <param name="mslMetres">The reading we hold, always orthometric.</param>
    /// <param name="geoidSeparationM">GGA's separation for where the reading
    /// was taken, or null when we have none.</param>
    /// <param name="wantsHae">Whether the role asks for height above the
    /// ellipsoid.</param>
    public static (int? AltitudeM, bool IsMsl) ForTransmit(int? mslMetres, int? geoidSeparationM, bool wantsHae)
    {
        if (mslMetres is not int metres) return (null, true);
        if (wantsHae && geoidSeparationM is int separation) return (metres + separation, false);
        return (metres, true);
    }
}
