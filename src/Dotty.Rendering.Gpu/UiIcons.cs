using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>Lucide outlines in a 24x24 coordinate system. Stroke with width 2 and round caps/joins.</summary>
internal static class UiIcons
{
    // Authentic upstream SVGs and full license are embedded; Icons/provenance.json pins their source/hash.
    private static readonly SKPath[] Paths = LoadPaths();

    /// <summary>Returns a process-lifetime borrowed path. Callers MUST NOT mutate or dispose it.</summary>
    public static SKPath GetPath(UiIcon icon)
    {
        int index = (int)icon;
        if ((uint)index >= (uint)Paths.Length)
            throw new ArgumentOutOfRangeException(nameof(icon), icon, "Unknown UI icon.");
        return Paths[index];
    }

    private static SKPath[] LoadPaths()
    {
        string[] names = ["copy", "copy-check", "arrow-up", "arrow-down", "clipboard-paste",
            "square-dashed", "columns-2", "rows-2", "plus", "x", "eraser", "chevron-up",
            "chevron-down"];
        var paths = new SKPath[names.Length + 1];
        paths[0] = new SKPath();
        try
        {
            for (int i = 0; i < names.Length; i++)
                paths[i + 1] = LoadPath(names[i]);
            return paths;
        }
        catch
        {
            foreach (SKPath? path in paths)
                path?.Dispose();
            throw;
        }
    }

    private static SKPath LoadPath(string name)
    {
        string resource = $"Dotty.Rendering.Gpu.Icons.{name}.svg";
        using Stream stream = typeof(UiIcons).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing UI icon resource: {resource}");
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        XElement root = XElement.Load(reader);
        XNamespace svg = "http://www.w3.org/2000/svg";
        if (root.Name != svg + "svg")
            throw new InvalidOperationException($"Invalid SVG root in {resource}.");
        ValidateAttributes(root, "width", "height", "viewBox", "fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin");
        if ((string?)root.Attribute("viewBox") != "0 0 24 24" ||
            (string?)root.Attribute("fill") != "none" ||
            (string?)root.Attribute("stroke") != "currentColor" ||
            (string?)root.Attribute("stroke-width") != "2" ||
            (string?)root.Attribute("stroke-linecap") != "round" ||
            (string?)root.Attribute("stroke-linejoin") != "round")
            throw new InvalidOperationException($"Unsupported SVG coordinate system or stroke style in {resource}.");

        var path = new SKPathBuilder();
        try
        {
            foreach (XElement element in root.Elements())
            {
                if (element.Name.Namespace != svg || element.HasElements)
                    throw new InvalidOperationException($"Unsupported SVG element: {element.Name}");
                switch (element.Name.LocalName)
                {
                    case "path":
                        ValidateAttributes(element, "d");
                        using (SKPath part = SKPath.ParseSvgPathData(Required(element, "d"))
                            ?? throw new InvalidOperationException($"Invalid SVG path in {resource}."))
                            path.AddPath(part);
                        break;
                    case "rect":
                        ValidateAttributes(element, "x", "y", "width", "height", "rx", "ry");
                        float x = Number(element, "x", 0);
                        float y = Number(element, "y", 0);
                        float width = Number(element, "width");
                        float height = Number(element, "height");
                        float rx = Number(element, "rx", Number(element, "ry", 0));
                        float ry = Number(element, "ry", rx);
                        if (width <= 0 || height <= 0 || rx < 0 || ry < 0)
                            throw new InvalidOperationException($"Invalid SVG rectangle in {resource}.");
                        path.AddRoundRect(SKRect.Create(x, y, width, height),
                            Math.Min(rx, width / 2), Math.Min(ry, height / 2));
                        break;
                    case "circle":
                        ValidateAttributes(element, "cx", "cy", "r");
                        float radius = Number(element, "r");
                        if (radius <= 0)
                            throw new InvalidOperationException($"Invalid SVG circle in {resource}.");
                        path.AddCircle(Number(element, "cx", 0), Number(element, "cy", 0), radius);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported SVG element {element.Name} in {resource}.");
                }
            }
            return path.Detach();
        }
        finally
        {
            path.Dispose();
        }
    }

    private static void ValidateAttributes(XElement element, params string[] allowed)
    {
        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
                continue;
            if (attribute.Name.Namespace != XNamespace.None ||
                Array.IndexOf(allowed, attribute.Name.LocalName) < 0)
                throw new InvalidOperationException($"Unsupported SVG attribute {attribute.Name} on {element.Name}.");
        }
    }

    private static string Required(XElement element, string name) =>
        (string?)element.Attribute(name)
        ?? throw new InvalidOperationException($"Missing SVG attribute {name} on {element.Name}.");

    private static float Number(XElement element, string name, float? fallback = null)
    {
        string? text = (string?)element.Attribute(name);
        if (text is null && fallback.HasValue)
            return fallback.Value;
        if (text is null || !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
            !float.IsFinite(value))
            throw new InvalidOperationException($"Invalid SVG number {name} on {element.Name}.");
        return value;
    }
}
