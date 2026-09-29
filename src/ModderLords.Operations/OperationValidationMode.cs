using System;
using System.Collections.Generic;

namespace ModderLords.Operations;

/// <summary>
/// Deliberate escape hatch for isolated native acceptance of the version-pinned Bellum adapter. It does not alter
/// shipped contract metadata and accepts no other provider or contract. Normal launches leave this variable unset.
/// </summary>
public static class OperationValidationMode
{
    public const string EnvironmentVariable = "MODDERLORDS_BELLUM_VALIDATION";
    public const string Bellum131Token = "bellum-civile-1.3.1-isolated";
    private static readonly HashSet<string> BellumContracts = new HashSet<string>(StringComparer.Ordinal)
    {
        "bellum-civile.state",
        "bellum-civile.authority",
        "bellum-civile.commands",
    };

    public static bool Allows(string? contractId)
        => Allows(contractId, Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static bool Allows(string? contractId, string? token)
        => string.Equals(token, Bellum131Token, StringComparison.Ordinal) &&
           contractId != null && BellumContracts.Contains(contractId);
}
