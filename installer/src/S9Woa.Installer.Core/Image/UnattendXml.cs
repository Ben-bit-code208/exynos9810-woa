// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Xml.Linq;

namespace S9Woa.Installer.Core.Image;

/// <summary>Choices that shape the generated OOBE answer file.</summary>
public sealed record UnattendOptions
{
    public string Username { get; init; } = "S9";
    public string? Password { get; init; }
    public string Locale { get; init; } = "en-US";
    public string TimeZone { get; init; } = "UTC";
    public string ComputerName { get; init; } = "GALAXY-S9";
    public bool AutoLogon { get; init; } = true;
}

/// <summary>
/// Generates a Windows Setup answer file that drives OOBE to the desktop without
/// user input: it creates a local administrator, skips the EULA, privacy,
/// Microsoft-account and wireless screens, and sets the locale, time zone and
/// computer name. Uses only the public unattend schema — no product internals.
/// Written into the offline image at <c>Windows\Panther\unattend.xml</c>, which
/// Windows processes during the oobeSystem pass.
/// </summary>
public static class UnattendXml
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    private const string Arch = "arm64";
    private const string PublicKeyToken = "31bf3856ad364e35"; // Well-known Microsoft component token.

    public static string RelativePath => @"Windows\Panther\unattend.xml";

    public static string Build(UnattendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Username))
        {
            throw new ArgumentException("A local account name is required.", nameof(options));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "unattend",
                new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName),
                OobeSystem(options)));
        using var writer = new StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    private static XElement Component(string name, params object[] content) =>
        new(Ns + "component",
            new XAttribute("name", name),
            new XAttribute("processorArchitecture", Arch),
            new XAttribute("publicKeyToken", PublicKeyToken),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"),
            content);

    private static XElement OobeSystem(UnattendOptions o)
    {
        var shell = Component("Microsoft-Windows-Shell-Setup",
            new XElement(Ns + "ComputerName", o.ComputerName),
            new XElement(Ns + "TimeZone", o.TimeZone),
            new XElement(Ns + "OOBE",
                new XElement(Ns + "HideEULAPage", "true"),
                new XElement(Ns + "HideOEMRegistrationScreen", "true"),
                new XElement(Ns + "HideOnlineAccountScreens", "true"),
                new XElement(Ns + "HideWirelessSetupInOOBE", "true"),
                new XElement(Ns + "ProtectYourPC", "3"),
                new XElement(Ns + "SkipMachineOOBE", "true"),
                new XElement(Ns + "SkipUserOOBE", "true")),
            UserAccounts(o));
        if (o.AutoLogon)
        {
            shell.Add(AutoLogon(o));
        }

        return new XElement(Ns + "settings",
            new XAttribute("pass", "oobeSystem"),
            Component("Microsoft-Windows-International-Core",
                new XElement(Ns + "InputLocale", o.Locale),
                new XElement(Ns + "SystemLocale", o.Locale),
                new XElement(Ns + "UILanguage", o.Locale),
                new XElement(Ns + "UserLocale", o.Locale)),
            shell);
    }

    private static XElement UserAccounts(UnattendOptions o) =>
        new(Ns + "UserAccounts",
            new XElement(Ns + "LocalAccounts",
                new XElement(Ns + "LocalAccount",
                    new XAttribute(Wcm + "action", "add"),
                    new XElement(Ns + "Name", o.Username),
                    new XElement(Ns + "DisplayName", o.Username),
                    new XElement(Ns + "Group", "Administrators"),
                    Password(o.Password))));

    private static XElement AutoLogon(UnattendOptions o) =>
        new(Ns + "AutoLogon",
            new XElement(Ns + "Enabled", "true"),
            new XElement(Ns + "LogonCount", "1"),
            new XElement(Ns + "Username", o.Username),
            Password(o.Password));

    private static XElement Password(string? value) =>
        new(Ns + "Password",
            new XElement(Ns + "Value", value ?? ""),
            new XElement(Ns + "PlainText", "true"));
}
