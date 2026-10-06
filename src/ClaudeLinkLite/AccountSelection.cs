namespace ClaudeLinkLite;

public static class AccountSelection
{
    public static string OfficialDefault(IReadOnlyList<Account> accounts, string configured)
    {
        if (accounts.Any(account => account.Path.Equals(configured, StringComparison.OrdinalIgnoreCase))) return configured;
        return accounts.FirstOrDefault()?.Path ?? "";
    }
}
