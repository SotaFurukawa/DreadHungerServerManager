namespace DreadHungerServerManager.Core.Configuration;

/// <summary>EC2 上の常駐サービス（Windows サービス）。GUI の検出・インストーラーと同じ名前を使う。</summary>
public static class HostService
{
    public const string Name = "DreadHungerServerManagerHost";

    public const string DisplayName = "Dread Hunger Server Manager Host";
}
