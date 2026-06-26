using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

public class BuildPostProcessor
{
    private const string ImmersivePackageName = "MedXR-Immersive";
    private const string ImmersivePackageVersion = "1.0.39.0";
    private const string ImmersivePhoneProductId = "1fe63616-fc08-48a6-9bd5-0b7f8c039059";
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

        ApplyImmersivePackageIdentity(document);
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
            "Prepared separate foreground HoloLens package '" +
            ImmersivePackageName +
            "' with PhoneProductId " +
            ImmersivePhoneProductId +
            ".");
    }

    private static void ApplyImmersivePackageIdentity(XmlDocument document)
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

        identity.Attributes["Name"].Value = ImmersivePackageName;
        identity.Attributes["Version"].Value = ImmersivePackageVersion;
        phoneIdentity.Attributes["PhoneProductId"].Value = ImmersivePhoneProductId;
    }
}
