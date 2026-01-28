using System;
using System.Reflection;
using Path = System.IO.Path;

var refPath = Path.Combine(
	Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
	".nuget", "packages", "avalonia", "11.3.8", "ref", "net8.0", "Avalonia.Base.dll");
var refAsm = Assembly.LoadFrom(refPath);
var refType = refAsm.GetType("Avalonia.AvaloniaLocator");
Console.WriteLine(refType?.FullName ?? "Ref type not found");
if (refType != null)
{
	var prop = refType.GetProperty("Current") ?? refType.GetProperty("CurrentMutable");
	Console.WriteLine(prop?.PropertyType.FullName);
	if (prop?.PropertyType != null)
	{
		foreach (var method in prop.PropertyType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
		{
			Console.WriteLine(method.Name);
		}
	}
}

var inputPlatformPath = Path.Combine(
	Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
	".nuget", "packages", "avalonia", "11.3.8", "ref", "net8.0", "Avalonia.Input.Platform.dll");
var inputAsm = Assembly.LoadFrom(inputPlatformPath);
var extType = inputAsm.GetType("Avalonia.Input.Platform.ClipboardExtensions");
if (extType != null)
{
	foreach (var method in extType.GetMethods(BindingFlags.Static | BindingFlags.Public))
	{
		Console.WriteLine(method.Name);
	}
}
