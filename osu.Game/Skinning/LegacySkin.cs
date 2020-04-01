// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Game.Audio;
using osu.Game.IO;
using osu.Game.Rulesets.Scoring;
using osuTK.Graphics;

namespace osu.Game.Skinning
{
    public class LegacySkin : Skin
    {
        [CanBeNull]
        protected TextureStore Textures;

        [CanBeNull]
        protected IResourceStore<SampleChannel> Samples;

        protected virtual bool AllowManiaSkin => true;

        public new LegacySkinConfiguration Configuration
        {
            get => base.Configuration as LegacySkinConfiguration;
            set => base.Configuration = value;
        }

        private readonly Dictionary<int, LegacyManiaSkinConfiguration> maniaConfigurations = new Dictionary<int, LegacyManiaSkinConfiguration>();

        public LegacySkin(SkinInfo skin, IResourceStore<byte[]> storage, AudioManager audioManager)
            : this(skin, new LegacySkinResourceStore<SkinFileInfo>(skin, storage), audioManager, "skin.ini")
        {
        }

        protected LegacySkin(SkinInfo skin, IResourceStore<byte[]> storage, AudioManager audioManager, string filename)
            : base(skin)
        {
            using (var stream = storage?.GetStream(filename))
            {
                if (stream != null)
                {
                    using (LineBufferedReader reader = new LineBufferedReader(stream, true))
                        Configuration = new LegacySkinDecoder().Decode(reader);

                    stream.Seek(0, SeekOrigin.Begin);

                    using (LineBufferedReader reader = new LineBufferedReader(stream))
                    {
                        var maniaList = new LegacyManiaSkinDecoder().Decode(reader);

                        foreach (var config in maniaList)
                            maniaConfigurations[config.Keys] = config;
                    }
                }
                else
                    Configuration = new LegacySkinConfiguration { LegacyVersion = LegacySkinConfiguration.LATEST_VERSION };
            }

            if (storage != null)
            {
                var samples = audioManager?.GetSampleStore(storage);
                if (samples != null)
                    samples.PlaybackConcurrency = OsuGameBase.SAMPLE_CONCURRENCY;

                Samples = samples;
                Textures = new TextureStore(new TextureLoaderStore(storage));

                (storage as ResourceStore<byte[]>)?.AddExtension("ogg");
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            Textures?.Dispose();
            Samples?.Dispose();
        }

        public override IBindable<TValue> GetConfig<TLookup, TValue>(TLookup lookup)
        {
            switch (lookup)
            {
                case GlobalSkinColours colour:
                    switch (colour)
                    {
                        case GlobalSkinColours.ComboColours:
                            var comboColours = Configuration.ComboColours;
                            if (comboColours != null)
                                return SkinUtils.As<TValue>(new Bindable<IReadOnlyList<Color4>>(comboColours));

                            break;

                        default:
                            return SkinUtils.As<TValue>(getCustomColour(colour.ToString()));
                    }

                    break;

                case LegacySkinConfiguration.LegacySetting legacy:
                    switch (legacy)
                    {
                        case LegacySkinConfiguration.LegacySetting.Version:
                            if (Configuration.LegacyVersion is decimal version)
                                return SkinUtils.As<TValue>(new Bindable<decimal>(version));

                            break;
                    }

                    break;

                case SkinCustomColourLookup customColour:
                    return SkinUtils.As<TValue>(getCustomColour(customColour.Lookup.ToString()));

                case LegacyManiaSkinConfigurationLookup maniaLookup:
                    if (!AllowManiaSkin)
                        return null;

                    if (!maniaConfigurations.TryGetValue(maniaLookup.Keys, out var existing))
                        maniaConfigurations[maniaLookup.Keys] = existing = new LegacyManiaSkinConfiguration(maniaLookup.Keys);

                    switch (maniaLookup.Lookup)
                    {
                        case LegacyManiaSkinConfigurationLookups.ColumnWidth:
                            Debug.Assert(maniaLookup.TargetColumn != null);
                            return SkinUtils.As<TValue>(new Bindable<float>(existing.ColumnWidth[maniaLookup.TargetColumn.Value]));

                        case LegacyManiaSkinConfigurationLookups.ColumnSpacing:
                            Debug.Assert(maniaLookup.TargetColumn != null);
                            return SkinUtils.As<TValue>(new Bindable<float>(existing.ColumnSpacing[maniaLookup.TargetColumn.Value]));

                        case LegacyManiaSkinConfigurationLookups.HitPosition:
                            return SkinUtils.As<TValue>(new Bindable<float>(existing.HitPosition));

                        case LegacyManiaSkinConfigurationLookups.ShowJudgementLine:
                            return SkinUtils.As<TValue>(new Bindable<bool>(existing.ShowJudgementLine));
                    }

                    break;

                default:
                    // handles lookups like GlobalSkinConfiguration

                    try
                    {
                        if (Configuration.ConfigDictionary.TryGetValue(lookup.ToString(), out var val))
                        {
                            // special case for handling skins which use 1 or 0 to signify a boolean state.
                            if (typeof(TValue) == typeof(bool))
                                val = val == "1" ? "true" : "false";

                            var bindable = new Bindable<TValue>();
                            if (val != null)
                                bindable.Parse(val);
                            return bindable;
                        }
                    }
                    catch
                    {
                    }

                    break;
            }

            return null;
        }

        private IBindable<Color4> getCustomColour(string lookup) => Configuration.CustomColours.TryGetValue(lookup, out var col) ? new Bindable<Color4>(col) : null;

        public override Drawable GetDrawableComponent(ISkinComponent component)
        {
            switch (component)
            {
                case GameplaySkinComponent<HitResult> resultComponent:
                    switch (resultComponent.Component)
                    {
                        case HitResult.Miss:
                            return this.GetAnimation("hit0", true, false);

                        case HitResult.Meh:
                            return this.GetAnimation("hit50", true, false);

                        case HitResult.Good:
                            return this.GetAnimation("hit100", true, false);

                        case HitResult.Great:
                            return this.GetAnimation("hit300", true, false);
                    }

                    break;
            }

            return this.GetAnimation(component.LookupName, false, false);
        }

        public override Texture GetTexture(string componentName)
        {
            componentName = getFallbackName(componentName);

            float ratio = 2;
            var texture = Textures?.Get($"{componentName}@2x");

            if (texture == null)
            {
                ratio = 1;
                texture = Textures?.Get(componentName);
            }

            if (texture != null)
                texture.ScaleAdjust = ratio;

            return texture;
        }

        public override SampleChannel GetSample(ISampleInfo sampleInfo)
        {
            foreach (var lookup in sampleInfo.LookupNames)
            {
                var sample = Samples?.Get(lookup);

                if (sample != null)
                    return sample;
            }

            if (sampleInfo is HitSampleInfo hsi)
                // Try fallback to non-bank samples.
                return Samples?.Get(hsi.Name);

            return null;
        }

        private string getFallbackName(string componentName)
        {
            string lastPiece = componentName.Split('/').Last();
            return componentName.StartsWith("Gameplay/taiko/") ? "taiko-" + lastPiece : lastPiece;
        }
    }
}
