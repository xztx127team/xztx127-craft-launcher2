using XCL2.App.Models;

namespace XCL2.App.Services;

internal static class AccountUpdatePolicy
{
    internal static Account? FindExisting(IEnumerable<Account> accounts, Account incoming)
    {
        var sameId = string.IsNullOrWhiteSpace(incoming.Id) ? null
            : accounts.FirstOrDefault(account => account.Id == incoming.Id);
        if (sameId != null) return sameId;
        if (incoming.Type is not (AccountType.Microsoft or AccountType.AuthServer) || !Guid.TryParse(incoming.Uuid, out var incomingUuid))
            return null;
        // Reauthenticating after a restart replaces the retained profile rather
        // than adding another row or losing its selected state and local edits.
        return accounts.FirstOrDefault(account => account.Type == incoming.Type &&
            Guid.TryParse(account.Uuid, out var existingUuid) && existingUuid == incomingUuid &&
            (incoming.Type != AccountType.AuthServer || string.Equals(account.AuthServerApiRoot?.TrimEnd('/'),
                incoming.AuthServerApiRoot?.TrimEnd('/'), StringComparison.Ordinal)));
    }

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
