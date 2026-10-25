using XCL2.App.Models;

namespace XCL2.App.Services;

internal static class AccountUpdatePolicy
{
    internal static void PreserveLocalPreferences(Account incoming, Account existing)
    {
        incoming.CreatedAtUtc = existing.CreatedAtUtc;
        if (ReferenceEquals(incoming, existing) || incoming.Type != existing.Type) return;
        // A refreshed online identity contains credentials/profile only. It must not reset
        // user-selected avatar, skin preferences or account selection with DTO defaults.
        if (incoming.Type is AccountType.Microsoft or AccountType.AuthServer)
        {
            incoming.AvatarPhotoPath = existing.AvatarPhotoPath;
            incoming.SkinType = existing.SkinType;
            incoming.CustomSkinPath = existing.CustomSkinPath;
            incoming.CustomSkinSlim = existing.CustomSkinSlim;
            incoming.IsSelected = existing.IsSelected;
        }
    }
}
