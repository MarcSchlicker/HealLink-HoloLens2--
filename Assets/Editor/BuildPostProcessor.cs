using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

public class BuildPostProcessor
{
    private const string AppIconAssetPath = "Assets/AppIcons/HealLinkTraineeIcon.png";
    private const string PackageName = "HealLink-Trainee";
    private const string PackageVersion = "1.0.39.0";
    private const string PhoneProductId = "1fe63616-fc08-48a6-9bd5-0b7f8c039059";
    private const string FoundationNamespace =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private const string PhoneNamespace =
        "http://schemas.microsoft.com/appx/2014/phone/manifest";
    private const string RestrictedCapabilityNamespace =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    public static void AddCapability(
        XmlDocument document,
        string elementName,
        string capability,
        string namespaceUri,
        bool append)
    {
        XmlNode capabilities = document.DocumentElement.GetElementsByTagName("Capabilities")[0];
        foreach (XmlNode childNode in capabilities.ChildNodes)
        {
            XmlAttribute nameAttribute = childNode.Attributes?["Name"];
            if (childNode.Name == elementName &&
                nameAttribute != null &&
                nameAttribute.Value == capability)
            {
                return;
            }
        }

        XmlElement element = document.CreateElement(elementName, namespaceUri);
        element.SetAttribute("Name", capability);
        if (append)
        {
            capabilities.AppendChild(element);
        }
        else
        {
            capabilities.PrependChild(element);
        }
    }

    public static void AddNamespace(XmlDocument document, string name, string uri)
    {
        document.DocumentElement.SetAttribute(name, uri);
    }

    [PostProcessBuildAttribute(1)]
    public static void OnPostprocessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.WSAPlayer)
        {
            return;
        }

        string[] solutionFiles = Directory.GetFiles(pathToBuiltProject, "*.sln");
        if (solutionFiles.Length == 0)
        {
            throw new BuildPlayerWindow.BuildMethodException(
                "The generated UWP solution could not be found below: " + pathToBuiltProject);
        }

        string projectName = Path.GetFileNameWithoutExtension(solutionFiles[0]);
        string manifestPath = Path.Combine(
            pathToBuiltProject,
            projectName,
            "Package.appxmanifest");

        XmlDocument document = new XmlDocument();
        document.PreserveWhitespace = true;
        document.Load(manifestPath);

        ApplyPackageIdentity(document);
        ReplaceLogoAssets(pathToBuiltProject, projectName);
        AddNamespace(document, "xmlns:rescap", RestrictedCapabilityNamespace);
        AddCapability(
            document,
            "rescap:Capability",
            "perceptionSensorsExperimental",
            RestrictedCapabilityNamespace,
            false);
        AddCapability(
            document,
            "DeviceCapability",
            "backgroundSpatialPerception",
            FoundationNamespace,
            true);
        document.Save(manifestPath);

        Debug.Log(
            "Prepared HoloLens package '" +
            PackageName +
            "' with PhoneProductId " +
            PhoneProductId +
            ".");
    }

    private static void ReplaceLogoAssets(string pathToBuiltProject, string projectName)
    {
        string iconPath = Path.GetFullPath(AppIconAssetPath);
        string assetsDirectory = Path.Combine(pathToBuiltProject, projectName, "Assets");

        if (!File.Exists(iconPath) || !Directory.Exists(assetsDirectory))
        {
            Debug.LogWarning("Could not apply HealLink Trainee app icon because the icon or build asset folder was not found.");
            return;
        }

        Texture2D source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!source.LoadImage(File.ReadAllBytes(iconPath)))
        {
            Debug.LogWarning("Could not read HealLink Trainee app icon: " + iconPath);
            Object.DestroyImmediate(source);
            return;
        }

        WriteSquareLogo(source, Path.Combine(assetsDirectory, "Square150x150Logo.scale-200.png"), 300);
        WriteSquareLogo(source, Path.Combine(assetsDirectory, "Square44x44Logo.scale-200.png"), 88);
        WriteSquareLogo(source, Path.Combine(assetsDirectory, "Square44x44Logo.targetsize-24.png"), 24);
        WriteSquareLogo(source, Path.Combine(assetsDirectory, "Square44x44Logo.targetsize-24_altform-unplated.png"), 24);
        WriteSquareLogo(source, Path.Combine(assetsDirectory, "StoreLogo.png"), 50);
        WriteWideLogo(source, Path.Combine(assetsDirectory, "Wide310x150Logo.scale-200.png"), 620, 300);

        Object.DestroyImmediate(source);
    }

    private static void WriteSquareLogo(Texture2D source, string outputPath, int size)
    {
        Texture2D logo = ResizeSquare(source, size, size);
        File.WriteAllBytes(outputPath, logo.EncodeToPNG());
        Object.DestroyImmediate(logo);
    }

    private static void WriteWideLogo(Texture2D source, string outputPath, int width, int height)
    {
        Texture2D icon = ResizeSquare(source, height, height);
        Texture2D logo = new Texture2D(width, height, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[width * height];

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = Color.white;
        }

        logo.SetPixels(pixels);
        logo.SetPixels((width - height) / 2, 0, height, height, icon.GetPixels());
        logo.Apply();
        File.WriteAllBytes(outputPath, logo.EncodeToPNG());

        Object.DestroyImmediate(icon);
        Object.DestroyImmediate(logo);
    }

    private static Texture2D ResizeSquare(Texture2D source, int width, int height)
    {
        Texture2D output = new Texture2D(width, height, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[width * height];
        float side = Mathf.Min(source.width, source.height);
        float offsetX = (source.width - side) * 0.5f;
        float offsetY = (source.height - side) * 0.5f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = (offsetX + ((x + 0.5f) * side / width)) / source.width;
                float v = (offsetY + ((y + 0.5f) * side / height)) / source.height;
                pixels[y * width + x] = source.GetPixelBilinear(u, v);
            }
        }

        output.SetPixels(pixels);
        output.Apply();
        return output;
    }

    private static void ApplyPackageIdentity(XmlDocument document)
    {
        XmlNode identity = document.GetElementsByTagName(
            "Identity",
            FoundationNamespace)[0];
        XmlNode phoneIdentity = document.GetElementsByTagName(
            "PhoneIdentity",
            PhoneNamespace)[0];

        if (identity == null || phoneIdentity == null)
        {
            throw new BuildPlayerWindow.BuildMethodException(
                "The generated UWP manifest is missing Identity or PhoneIdentity.");
        }

        identity.Attributes["Name"].Value = PackageName;
        identity.Attributes["Version"].Value = PackageVersion;
        phoneIdentity.Attributes["PhoneProductId"].Value = PhoneProductId;
    }
}
