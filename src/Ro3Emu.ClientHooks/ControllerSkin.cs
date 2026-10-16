using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>Optional PNG art in &lt;client&gt;/ro3emu/skin. Images are checked once a second and fall back to
    /// their built-in sprites when a file is absent or cannot be loaded.</summary>
    internal static class ControllerSkin
    {
        private sealed class Asset
        {
            public DateTime Modified;
            public long Length = -1;
            public Sprite Sprite;
            public Texture2D Texture;
        }

        private sealed class Target
        {
            public Image Image;
            public string Name;
            public Sprite Fallback;
            public Color FallbackColor;
        }

        private static readonly Dictionary<string, Asset> Assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
        private static readonly List<Target> Targets = new List<Target>();
        private static string _directory;
        private static float _nextCheck;

        private static string DirectoryPath => _directory ??= Path.Combine(Path.GetDirectoryName(Application.dataPath), "ro3emu", "skin");

        internal static void Bind(Image image, string name, Sprite fallback)
        {
            Targets.Add(new Target { Image = image, Name = name, Fallback = fallback, FallbackColor = image.color });
            image.sprite = Get(name, fallback);
            _nextCheck = 0f;
        }

        internal static Sprite Get(string name, Sprite fallback)
        {
            if (!Assets.TryGetValue(name, out var asset)) Assets[name] = asset = new Asset();
            return asset.Sprite != null ? asset.Sprite : fallback;
        }

        internal static void Tick()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            foreach (var pair in Assets)
            {
                string path = Path.Combine(DirectoryPath, pair.Key + ".png");
                DateTime modified = DateTime.MinValue;
                long length = -1;
                try
                {
                    if (File.Exists(path))
                    {
                        var info = new FileInfo(path);
                        modified = info.LastWriteTimeUtc;
                        length = info.Length;
                    }
                    var asset = pair.Value;
                    if (asset.Modified == modified && asset.Length == length) continue;
                    asset.Modified = modified;
                    asset.Length = length;
                    var previousSprite = asset.Sprite;
                    var previousTexture = asset.Texture;
                    asset.Sprite = null;
                    asset.Texture = null;
                    if (length > 0)
                    {
                        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (ImageConversion.LoadImage(texture, File.ReadAllBytes(path)))
                        {
                            texture.name = "ro3emu_skin_" + pair.Key;
                            texture.wrapMode = TextureWrapMode.Clamp;
                            asset.Texture = texture;
                            asset.Sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                                new Vector2(0.5f, 0.5f), 100f);
                            Debug.Log("[Ro3Emu.Skin] loaded " + pair.Key + ".png (" + texture.width + "x" + texture.height + ")");
                        }
                        else { UnityEngine.Object.Destroy(texture); Debug.LogWarning("[Ro3Emu.Skin] invalid PNG: " + path); }
                    }
                    else if (previousSprite != null) Debug.Log("[Ro3Emu.Skin] removed " + pair.Key + ".png; using built-in art");
                    if (previousSprite != null) UnityEngine.Object.Destroy(previousSprite);
                    if (previousTexture != null) UnityEngine.Object.Destroy(previousTexture);
                }
                catch (Exception error)
                {
                    Debug.LogWarning("[Ro3Emu.Skin] " + pair.Key + ": " + error.Message);
                }
            }
            for (int i = Targets.Count - 1; i >= 0; i--)
            {
                var target = Targets[i];
                if (target.Image == null) { Targets.RemoveAt(i); continue; }
                var sprite = Get(target.Name, target.Fallback);
                if (target.Image.sprite != sprite) target.Image.sprite = sprite;
                var color = Assets[target.Name].Sprite != null ? Color.white : target.FallbackColor;
                if (target.Image.color != color) target.Image.color = color;
            }
        }
    }
}
