using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UserDashboard.Services;

/// <summary>
/// Password rules + secure random generator. Mirrors the
/// $PasswordRules table, Validate-Password, and New-RandomPassword
/// helpers from the PowerShell file.
/// </summary>
internal static class PasswordHelper
{
    public sealed record Rule(string Key, string Label, Func<string, bool> Test);

    public static readonly IReadOnlyList<Rule> Rules = new Rule[]
    {
        new("len",   $"minimum {Constants.MinPasswordLength} characters",
            p => (p ?? "").Length >= Constants.MinPasswordLength),
        new("upper", "Uppercase letter", p => Regex.IsMatch(p ?? "", "[A-Z]")),
        new("lower", "Lowercase letter", p => Regex.IsMatch(p ?? "", "[a-z]")),
        new("digit", "Number",           p => Regex.IsMatch(p ?? "", "\\d")),
    };

    /// <summary>Returns labels of failing rules; empty list = password is valid.</summary>
    public static List<string> Validate(string? password)
    {
        var missing = new List<string>();
        var pwd = password ?? string.Empty;
        foreach (var r in Rules)
        {
            if (!r.Test(pwd)) missing.Add(r.Label);
        }
        return missing;
    }

    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Digit = "0123456789";

    /// <summary>Generates a random password that satisfies all rules.</summary>
    public static string Generate(int length = Constants.MinPasswordLength)
    {
        if (length < Constants.MinPasswordLength)
            throw new ArgumentException(
                $"Password length must be at least {Constants.MinPasswordLength} characters.",
                nameof(length));

        string all = Upper + Lower + Digit;

        // Guarantee one of each required class, then fill the rest randomly.
        var chars = new List<char>(length)
        {
            Upper[RandomIndex(Upper.Length)],
            Lower[RandomIndex(Lower.Length)],
            Digit[RandomIndex(Digit.Length)],
        };
        while (chars.Count < length)
        {
            chars.Add(all[RandomIndex(all.Length)]);
        }

        // Cryptographically-secure Fisher-Yates shuffle so the guaranteed
        // chars are not always at the front.
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = RandomIndex(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        var sb = new StringBuilder(chars.Count);
        foreach (var c in chars) sb.Append(c);
        return sb.ToString();
    }

    private static int RandomIndex(int exclusiveMax)
    {
        // RandomNumberGenerator.GetInt32 only exists on .NET Core 3.0+ /
        // .NET 5+ / .NET Standard 2.1, so on .NET Framework 4.8 we emulate
        // it with rejection sampling to avoid modulo bias.
        if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        byte[] buf = new byte[4];
        int maxUnbiased = int.MaxValue - (int.MaxValue % exclusiveMax);
        while (true)
        {
            _rng.GetBytes(buf);
            int val = BitConverter.ToInt32(buf, 0) & int.MaxValue;
            if (val < maxUnbiased) return val % exclusiveMax;
        }
    }

    private static readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();
}
