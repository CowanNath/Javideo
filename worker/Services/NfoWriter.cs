using System.Text;
using System.Web;
using System.Xml;
using Javideo.Worker.Models;

namespace Javideo.Worker.Services;

/// <summary>
/// Writes an Emby/Jellyfin-compatible .nfo for an ingested movie.
/// </summary>
public sealed class NfoWriter
{
    public void Write(string destNfoPath, Movie m, IEnumerable<MagnetResult> magnets)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false
        };

        Directory.CreateDirectory(Path.GetDirectoryName(destNfoPath)!);

        using var xw = XmlWriter.Create(destNfoPath, settings);
        xw.WriteStartElement("movie");

        xw.WriteElementString("title", m.Title ?? m.Number);
        xw.WriteElementString("originaltitle", m.OriginalTitle ?? m.Title ?? m.Number);
        xw.WriteElementString("num", m.Number);
        xw.WriteElementString("customrating", "JP-18");

        if (!string.IsNullOrWhiteSpace(m.Maker))
            xw.WriteElementString("studio", m.Maker);
        if (!string.IsNullOrWhiteSpace(m.Maker))
            xw.WriteElementString("maker", m.Maker);
        if (!string.IsNullOrWhiteSpace(m.Label))
            xw.WriteElementString("label", m.Label);
        if (!string.IsNullOrWhiteSpace(m.Series))
            xw.WriteElementString("series", m.Series);
        if (!string.IsNullOrWhiteSpace(m.Director))
            xw.WriteElementString("director", m.Director);
        if (!string.IsNullOrWhiteSpace(m.ReleaseDate))
            xw.WriteElementString("premiered", m.ReleaseDate);
        if (!string.IsNullOrWhiteSpace(m.ReleaseDate))
            xw.WriteElementString("releasedate", m.ReleaseDate);
        if (m.RuntimeMinutes is int rt)
            xw.WriteElementString("runtime", rt.ToString());
        if (!string.IsNullOrWhiteSpace(m.Summary))
            xw.WriteElementString("plot", m.Summary);
        if (!string.IsNullOrWhiteSpace(m.Provider))
            xw.WriteElementString("website", m.Provider);

        foreach (var a in m.Actors)
        {
            xw.WriteStartElement("actor");
            xw.WriteElementString("name", a.Name);
            if (!string.IsNullOrWhiteSpace(a.AvatarUrl))
                xw.WriteElementString("thumb", a.AvatarUrl);
            xw.WriteEndElement();
        }

        foreach (var t in m.Tags)
        {
            xw.WriteStartElement("tag");
            xw.WriteAttributeString("category", t.Category);
            xw.WriteString(t.Name);
            xw.WriteEndElement();
        }

        xw.WriteEndElement(); // movie
        xw.Flush();
    }
}
