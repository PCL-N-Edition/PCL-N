// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Text;

namespace PCL.Desktop.Legal;

/// <summary>
/// Loads embedded PCL-N-Edition terms / privacy documents and tracks acceptance version.
/// </summary>
internal static class EmbeddedLegalDocuments
{
    /// <summary>Bump when legal text materially changes so users re-accept.</summary>
    public const string DocumentVersion = "v0.1";

    public const string SettingsKeyAcceptedVersion = "LegalAcceptedVersion";

    private const string TermsResource = "PCL.Desktop.Legal.terms.md";
    private const string PrivacyResource = "PCL.Desktop.Legal.privacy.md";

    public static string LoadTermsMarkdown() => LoadResource(TermsResource);

    public static string LoadPrivacyMarkdown() => LoadResource(PrivacyResource);

    public static string BuildFirstRunAcceptanceMarkdown()
    {
        string terms = LoadTermsMarkdown().Trim();
        string privacy = LoadPrivacyMarkdown().Trim();
        return
            "在继续使用 PCL N Edition 官方客户端前，请阅读并同意下列协议。\n\n" +
            "不同意可退出程序。协议全文如下（可滚动阅读）：\n\n" +
            "---\n\n" +
            terms +
            "\n\n---\n\n" +
            privacy;
    }

    private static string LoadResource(string logicalName)
    {
        Assembly assembly = typeof(EmbeddedLegalDocuments).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(logicalName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded legal document missing: {logicalName}");
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
