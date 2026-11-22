using System.Globalization;
using System.Text;

namespace Search.Core;

// A passwords file another browser exported, read (the CSV half of ImportFile.swift and Vault.take(csv:)): the way in
// for a browser whose own files can't be read, which on Windows is Chrome since 127 (app-bound encryption), a Gecko
// browser with a primary password, or another PC. Chrome, Edge, Brave and Opera write name,url,username,password,note;
// Firefox url,username,password,httpRealm,formActionOrigin,guid,timeCreated,timeLastUsed,timePasswordChanged; password
// managers their own. So columns are found by what they are called, loosely (case, spaces, underscores and dashes
// don't count), the separator by what the header uses, and quoting is taken as it comes: quoted fields, doubled
// quotes inside them, newlines inside those, a stray quote in the middle of a field kept as it is. Read once; the file
// is yours to delete, and nothing here keeps a copy of it.

/// What a passwords file held: the rows with a website and a password, and how many rows didn't have both.
/// `Recognised`: a header naming an address and a password was found at all.
public sealed record CsvLogins(IReadOnlyList<ImportLogin> Logins, int Skipped, bool Recognised);

public static class PasswordCsv
{
    private static readonly string[] UrlNames = ["url", "loginuri", "loginurl", "website", "websiteurl", "webaddress", "site", "siteurl", "origin", "originurl", "hostname", "host", "uri", "address"];
    private static readonly string[] UserNames = ["username", "loginusername", "user", "usernamevalue", "login", "email", "emailaddress", "userid", "account"];
    private static readonly string[] PasswordNames = ["password", "loginpassword", "passwordvalue", "pass", "pwd"];
    private static readonly string[] UsedNames = ["timelastused", "lastused"];

    /// A file read through the size cap, as UTF-8 (a byte-order mark of any kind honoured).
    public static CsvLogins ReadFile(string file)
    {
        using var handle = ImportRead.OpenRegular(file, limit: ImportRead.JsonLimit);
        using var stream = new FileStream(handle, FileAccess.Read, 1);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return Read(reader.ReadToEnd());
    }

    public static CsvLogins Read(string text)
    {
        text = text.TrimStart('﻿');
        var rows = Parse(text, Separator(text));
        if (rows.Count == 0) return new([], 0, false);
        var header = rows[0].Select(Plain).ToList();
        int Column(string[] names) => names.Select(n => header.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);
        int urlAt = Column(UrlNames), userAt = Column(UserNames), passAt = Column(PasswordNames), usedAt = Column(UsedNames);
        if (urlAt < 0 || passAt < 0) return new([], rows.Count - 1, false);
        var logins = new List<ImportLogin>();
        var seen = new HashSet<string>();
        var skipped = 0;
        foreach (var row in rows.Skip(1))
        {
            string Field(int at) => at >= 0 && at < row.Count ? row[at] : "";
            var origin = Field(urlAt).Trim();
            var password = Field(passAt);
            var host = Accounts.Host(origin);
            if (host.Length == 0 || password.Length == 0) { skipped++; continue; }
            var used = long.TryParse(Field(usedAt), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0 && ms < 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
            var login = new ImportLogin(origin, host, Field(userAt), password, origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase), used);
            if (seen.Add(login.Id + "\u0001" + password)) logins.Add(login); else skipped++;
        }
        return new(logins, skipped, true);
    }

    /// A column's name as compared: lowercase letters and digits only.
    private static string Plain(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// The separator the header line uses most, outside quotes: a comma unless it plainly isn't.
    internal static char Separator(string text)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '\n' or '\r') break;
            else if (!quoted) { if (c == ',') commas++; else if (c == ';') semicolons++; else if (c == '\t') tabs++; }
        }
        return tabs > commas && tabs >= semicolons ? '\t' : semicolons > commas ? ';' : ',';
    }

    /// Rows of fields. A quote opens a quoted field only at its start; inside one, a doubled quote is a quote, and a
    /// quote is the end only before a separator, a line's end or the file's; any other is kept. Blank lines are none.
    internal static List<List<string>> Parse(string text, char separator = ',')
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, started = false;
        void EndField() { row.Add(field.ToString()); field.Clear(); started = false; }
        void EndRow() { EndField(); if (row.Any(f => f.Length > 0)) rows.Add(row); row = []; }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') { field.Append(c); continue; }
                if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; continue; }
                // The end only if what follows, past any spaces, ends the field; the spaces aren't the field's either.
                var j = i + 1;
                while (j < text.Length && text[j] is ' ' or '\t' && text[j] != separator) j++;
                if (j >= text.Length || text[j] == separator || text[j] is '\r' or '\n') { quoted = false; i = j - 1; }
                else field.Append('"');
                continue;
            }
            // Spaces before the opening quote aren't the field's.
            if (c == '"' && !started) { quoted = true; started = true; field.Clear(); continue; }
            if (c == separator) { EndField(); continue; }
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            if (c is '\r' or '\n') { EndRow(); continue; }
            field.Append(c);
            if (c is not (' ' or '\t')) started = true;
        }
        EndRow();
        return rows;
    }
}
