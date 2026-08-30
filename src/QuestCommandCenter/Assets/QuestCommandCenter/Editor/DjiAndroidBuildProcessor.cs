using System;
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace QuestCommandCenter.Editor
{
    public sealed class DjiAndroidBuildProcessor : IPostGenerateGradleAndroidProject
    {
        private const string SecretName = "DJI_MSDK_APP_KEY";
        private const string GradleMarker = "// QUEST_COMMAND_CENTER_DJI_V4";

        public int callbackOrder => 1000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            InjectGradleDependencies(Path.Combine(path, "build.gradle"));
            InjectAppKey(Path.Combine(path, "src/main/AndroidManifest.xml"));
        }

        private static void InjectGradleDependencies(string buildGradlePath)
        {
            var gradle = File.ReadAllText(buildGradlePath);

            const string dependenciesAnchor = "dependencies {";
            var dependencies = @"dependencies {
    // QUEST_COMMAND_CENTER_DJI_V4";

            if (!gradle.Contains("androidx.multidex:multidex:2.0.0"))
            {
                dependencies += "\n    implementation 'androidx.multidex:multidex:2.0.0'";
            }

            if (!gradle.Contains("androidx.appcompat:appcompat:1.0.0"))
            {
                dependencies += "\n    implementation 'androidx.appcompat:appcompat:1.0.0'";
            }

            if (!gradle.Contains("androidx.constraintlayout:constraintlayout:1.1.3"))
            {
                dependencies += "\n    implementation 'androidx.constraintlayout:constraintlayout:1.1.3'";
            }

            if (!gradle.Contains("androidx.lifecycle:lifecycle-process:2.3.1"))
            {
                dependencies += "\n    implementation 'androidx.lifecycle:lifecycle-process:2.3.1'";
            }

            if (!gradle.Contains("com.dji:dji-sdk:4.18"))
            {
                dependencies += @"
    implementation('com.dji:dji-sdk:4.18') {
        exclude module: 'library-anti-distortion'
    }
    compileOnly 'com.dji:dji-sdk-provided:4.18'";
            }

            const string androidAnchor = "android {";
            const string androidSettings = @"android {
    useLibrary 'org.apache.http.legacy'";

            const string defaultConfigAnchor = "defaultConfig {";
            const string defaultConfigSettings = @"defaultConfig {
        multiDexEnabled true";

            const string packagingAnchor = "packagingOptions {";
            const string packagingSettings = @"packagingOptions {
        exclude 'META-INF/rxjava.properties'
        exclude 'assets/location_map_gps_locked.png'
        exclude 'assets/location_map_gps_3d.png'";

            if (dependencies != dependenciesAnchor + "\n    " + GradleMarker)
            {
                gradle = ReplaceRequired(gradle, dependenciesAnchor, dependencies, buildGradlePath);
            }

            if (!gradle.Contains("useLibrary 'org.apache.http.legacy'"))
            {
                gradle = ReplaceRequired(gradle, androidAnchor, androidSettings, buildGradlePath);
            }

            if (!gradle.Contains("multiDexEnabled true"))
            {
                gradle = ReplaceRequired(gradle, defaultConfigAnchor, defaultConfigSettings, buildGradlePath);
            }

            if (!gradle.Contains("exclude 'META-INF/rxjava.properties'"))
            {
                gradle = ReplaceRequired(gradle, packagingAnchor, packagingSettings, buildGradlePath);
            }

            File.WriteAllText(buildGradlePath, gradle);
        }

        private static void InjectAppKey(string manifestPath)
        {
            var appKey = ReadSecret();
            var document = new XmlDocument();
            document.PreserveWhitespace = true;
            document.Load(manifestPath);

            var namespaceManager = new XmlNamespaceManager(document.NameTable);
            namespaceManager.AddNamespace("android", "http://schemas.android.com/apk/res/android");
            var metadata = document.SelectSingleNode(
                "/manifest/application/meta-data[@android:name='com.dji.sdk.API_KEY']",
                namespaceManager) as XmlElement;
            if (metadata == null)
            {
                throw new InvalidOperationException(
                    "DJI App Key metadata was not found in the generated Android manifest.");
            }

            metadata.SetAttribute("value", "http://schemas.android.com/apk/res/android", appKey);
            document.Save(manifestPath);
            Debug.Log("Injected the local DJI MSDK App Key into the generated manifest (value not logged).");
        }

        private static string ReadSecret()
        {
            var environmentValue = Environment.GetEnvironmentVariable(SecretName);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return environmentValue.Trim();
            }

            var workspaceRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
            var secretFile = Path.Combine(workspaceRoot, ".env.local");
            if (!File.Exists(secretFile))
            {
                throw new InvalidOperationException(
                    "Missing local DJI App Key. Set DJI_MSDK_APP_KEY or create ignored .env.local from .env.example.");
            }

            foreach (var line in File.ReadAllLines(secretFile))
            {
                if (line.StartsWith(SecretName + "=", StringComparison.Ordinal))
                {
                    var value = line.Substring(SecretName.Length + 1).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            throw new InvalidOperationException("DJI_MSDK_APP_KEY is empty in the ignored local secret file.");
        }

        private static string ReplaceRequired(string source, string anchor, string replacement, string path)
        {
            if (!source.Contains(anchor))
            {
                throw new InvalidOperationException("Expected Gradle anchor '" + anchor + "' in " + path);
            }

            return source.Replace(anchor, replacement);
        }
    }
}
