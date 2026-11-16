using System;
using System.IO;
using System.Text;
using TorrentChecker;

public static class TorrentNameTests
{
    static int checks;
    static byte[] Bytes(string value) { return Encoding.UTF8.GetBytes(value); }
    static string StringValue(string value) { return Encoding.UTF8.GetByteCount(value) + ":" + value; }
    static string Torrent(string fields) { return "d4:infod" + fields + "ee"; }
    static void Check(byte[] input, string expected, string label)
    {
        string actual = mdlMain.GetTorrentName(input);
        if (actual != expected) throw new Exception("Torrent name: " + label + ": expected [" + expected + "], got [" + actual + "]");
        checks++;
    }
    static void Check(string input, string expected, string label) { Check(Bytes(input), expected, label); }
    public static int Run()
    {
        checks = 0;
        foreach (string name in new[] { "movie.mkv", "Пример фильма.mkv", "Film 🎬.mkv", "" })
            Check(Torrent("4:name" + StringValue(name)), name, "UTF-8 byte length " + name);
        string invalid = "a" + new string(Path.GetInvalidFileNameChars()) + "z";
        Check(Torrent("4:name" + StringValue(invalid)), "a" + new string('_', Path.GetInvalidFileNameChars().Length) + "z", "filename sanitization");
        // Binary strings are opaque, including delimiters, fake names and invalid UTF-8.
        byte[] pieces = new byte[] { 0, 255, 58, 110, 97, 109, 101, 49, 58, 88, 101, 100 };
        foreach (bool before in new[] { true, false })
        {
            using (var stream = new MemoryStream())
            {
                byte[] prefix = Bytes("d4:infod" + (before ? "4:name4:real" : "") + "6:pieces" + pieces.Length + ":");
                stream.Write(prefix, 0, prefix.Length);
                stream.Write(pieces, 0, pieces.Length);
                byte[] suffix = Bytes((before ? "" : "4:name4:real") + "ee");
                stream.Write(suffix, 0, suffix.Length);
                Check(stream.ToArray(), "real", "binary pieces ordering " + before);
            }
        }
        Check("d4:name4:fake1:xld4:name5:otherei123456789012345678901234567890ee4:infod1:xd4:name5:falsee4:name4:realee", "real", "nested false names and large integer");
        foreach (string fields in new[] { "4:name6:legacy10:name.utf-83:new", "10:name.utf-83:new4:name6:legacy", "10:name.utf-83:new" })
            Check(Torrent(fields), "new", "UTF-8 preference " + fields);
        foreach (string fields in new[] { "4:name6:legacy10:name.utf-80:", "10:name.utf-80:4:name6:legacy" })
            Check(Torrent(fields), "legacy", "empty UTF-8 fallback " + fields);
        byte[] badUtf8 = Bytes(Torrent("4:name1:X"));
        badUtf8[badUtf8.Length - 3] = 255;
        Check(badUtf8, "\uFFFD", "replacement decoding");
        Check((byte[])null, "", "null");
        foreach (string bad in new[] {
            "", "le", "0:", "de", "d4:infolee", "d4:infoi1ee", "d4:info0:e", "d4:infodee",
            "d4:infod4:namei1eee", Torrent("4:namele"), Torrent("4:namede"), "d4:infod10:name.utf-8i1eee",
            Torrent("4:name1:x4:name1:y"), Torrent("4:name1:x10:name.utf-81:y10:name.utf-81:z"),
            "d4:infod4:name1:xe4:infod4:name1:yee", Torrent("4:Name1:x"),
            Torrent("4:name999999999999999999999:x"), Torrent("4:name2147483648:x"), Torrent("4:name100:x"),
            Torrent("4:name01:x"), Torrent("4:name-1:x"), Torrent("4:name1x"), Torrent("4:name:x"),
            "d4:infod4:name1:x", Torrent("4:name1:x") + "e", Torrent("4:name1:x") + "0:",
            Torrent("4:name1:x1:zi-0e"), Torrent("4:name1:x1:zi01e"), Torrent("4:name1:x1:zie"),
            Torrent("4:name1:x1:zi+1e"), Torrent("4:name1:x1:zi1xe"), Torrent("4:name1:x1:zi1"),
            Torrent("4:name1:x1:zq"), Torrent("4:name1:x1:z"), Torrent("4:name1:xi1e0:"),
            Torrent("4:name1:x1:zl"), Torrent("4:name1:x1:zd1:ae"), Torrent("4:name1:x1:z9999999999:"),
            Torrent("4:name1:x1:z" + new string('l', 63) + new string('e', 63))
        }) Check(bad, "", "malformed " + bad);
        Check(Torrent("4:name1:x1:z" + new string('l', 62) + "i0e" + new string('e', 62)), "x", "64 containers allowed");
        Check(Torrent("4:name1:x1:zi-123e"), "x", "negative integer");
        // Every proper prefix of a valid torrent must be rejected, including after finding its name.
        byte[] valid = Bytes(Torrent("4:name4:real1:zld1:ai1eee"));
        Check(valid, "real", "complete trailing structure");
        for (int length = 0; length < valid.Length; length++)
        {
            var prefix = new byte[length];
            Array.Copy(valid, prefix, length);
            Check(prefix, "", "truncated prefix " + length);
        }
        return checks;
    }
}
