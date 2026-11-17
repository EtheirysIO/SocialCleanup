namespace SocialCleanup;

/// <summary>
/// The ONE game version this plugin is allowed to run against. Dalamud-free (linked into the test project).
/// </summary>
public static class SupportedGameVersion
{
    /// <summary>
    /// Exact game version (ffxivgame.ver format) every reverse-engineered signature, vtable slot and raw struct offset in
    /// this plugin was verified against: RemoveFriend + its call site (DirectFriendRemover), the RemoveFriend reply handler
    /// and InfoProxyFriendList.RequestData (ReloadSuppressor), InfoProxyFriendList.EndRequest (FriendListReader), the
    /// +0x3ADD / +0xC8 proxy bytes and the CharacterData.ExtraFlags request-state bits.
    ///
    /// DO NOT CHANGE THIS VALUE until every one of those signatures / offsets has been re-verified against the NEW
    /// ffxiv_dx11.exe. If the running game's version differs in any way the plugin refuses ALL functionality
    /// (see <see cref="Safety.VersionGate"/>); bumping this constant without re-verification removes that protection.
    /// </summary>
    public const string Value = "2026.11.03.0000.0000";
}
