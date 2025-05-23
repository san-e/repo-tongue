using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Realtime;
using REPOLib.Modules;
using Strobotnik.Klattersynth;
using UnityEngine;

namespace tongue;

[BepInPlugin("sane.tongue", "Tongue", "1.0.0")]
[BepInProcess("REPO.exe")]
[BepInIncompatibility("Lavighju.espeakTTS")]
[BepInDependency(REPOLib.MyPluginInfo.PLUGIN_GUID, BepInDependency.DependencyFlags.HardDependency)]
[HarmonyPatch]
public class Tongue : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    public static Tongue Instance { get; private set; }
    public static NetworkedEvent LanguageChangeEvent;
    private ConfigEntry<string> languageSetting;
    const int AUDIO_OUTPUT_SYNCHRONOUS = 0;
    const int espeakCHARS_UTF8 = 1;
    const int PHONEME_MODE_ASCII = 0;
    private string currentSpeaker = "";
    private Dictionary<string, string> languagePerPlayer = new Dictionary<string, string>();

    [DllImport("libespeak-ng.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int espeak_Initialize(int output, int buflength, string path, int options);

    [DllImport("libespeak-ng.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int espeak_SetVoiceByName(string name);

    [DllImport("libespeak-ng.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr espeak_TextToPhonemes(
        ref IntPtr textPtr,
        int textMode,
        int phonemeMode
    );

    public static readonly string[] language_list = new string[128]
    {
        "af",
        "am",
        "an",
        "ar",
        "as",
        "az",
        "ba",
        "be",
        "bg",
        "bn",
        "bpy",
        "bs",
        "ca",
        "chr",
        "cmn",
        "cs",
        "cu",
        "cy",
        "da",
        "de",
        "el",
        "en",
        "en-029",
        "en-gb-scotland",
        "en-gb-x-gbclan",
        "en-gb-x-gbcwmd",
        "en-gb-x-rp",
        "en-us",
        "eo",
        "es",
        "es-419",
        "et",
        "eu",
        "fa",
        "fa-latn",
        "fi",
        "fr",
        "fr-be",
        "fr-ch",
        "ga",
        "gd",
        "gn",
        "grc",
        "gu",
        "hak",
        "haw",
        "he",
        "hi",
        "hr",
        "ht",
        "hu",
        "hy",
        "hyw",
        "ia",
        "id",
        "io",
        "is",
        "it",
        "ja",
        "jbo",
        "ka",
        "kk",
        "kl",
        "kn",
        "ko",
        "kok",
        "ku",
        "ky",
        "la",
        "lb",
        "lfn",
        "lt",
        "ltg",
        "lv",
        "mi",
        "mk",
        "ml",
        "mr",
        "ms",
        "mt",
        "my",
        "nb",
        "nci",
        "ne",
        "nl",
        "nog",
        "om",
        "or",
        "pa",
        "pap",
        "piqd",
        "pl",
        "pt",
        "pt-br",
        "py",
        "qdb",
        "qu",
        "quc",
        "qya",
        "ro",
        "ru",
        "ru-lv",
        "sd",
        "shn",
        "si",
        "sjn",
        "sk",
        "sl",
        "smj",
        "sq",
        "sr",
        "sv",
        "sw",
        "ta",
        "te",
        "th",
        "tk",
        "tn",
        "tr",
        "tt",
        "ug",
        "uk",
        "ur",
        "uz",
        "vi",
        "vi-vn-x-central",
        "vi-vn-x-south",
        "yue",
    };

    public static string phoneticize(string text, string language)
    {
        // Retain default Klattersynth phoneticization when
        // English is selected
        if (language == "en")
        {
            return text;
        }
        espeak_SetVoiceByName(language);

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(text + '\0'); // null-terminated

        // Allocate unmanaged memory for UTF-8 text
        IntPtr unmanagedText = Marshal.AllocHGlobal(utf8Bytes.Length);
        Marshal.Copy(utf8Bytes, 0, unmanagedText, utf8Bytes.Length);

        // Pointer to pointer
        IntPtr textPtr = unmanagedText;

        // Get phonemes
        IntPtr phonemesPtr = espeak_TextToPhonemes(
            ref textPtr,
            espeakCHARS_UTF8,
            PHONEME_MODE_ASCII
        );
        if (phonemesPtr != IntPtr.Zero)
        {
            string phonemes = Marshal.PtrToStringAnsi(phonemesPtr);
            Marshal.FreeHGlobal(unmanagedText);
            return $"[{phonemes}]";
        }
        else
        {
            Marshal.FreeHGlobal(unmanagedText);
            return "";
        }
    }

    private void Awake()
    {
        Instance = this;
        Instance.gameObject.hideFlags = HideFlags.HideAndDontSave;
        Logger = base.Logger;
        LanguageChangeEvent = new NetworkedEvent("LanguageChangeEvent", HandleLanguageChangeEvent);

        languageSetting = Config.Bind(
            "General",
            "Language",
            "de",
            new ConfigDescription(
                "What language should the TTS emulate?",
                new AcceptableValueList<string>(language_list)
            )
        );

        string espeakNgDataLocation;
        try
        {
            espeakNgDataLocation = Directory
                .GetFiles(Paths.PluginPath, "phondata-manifest", SearchOption.AllDirectories)[0]
                .Replace("phondata-manifest", "");
        }
        catch
        {
            Logger.LogError("espeak-ng-data not found!");
            return;
        }

        Logger.LogInfo("Attempting to load eSpeak data from " + espeakNgDataLocation);
        int result = espeak_Initialize(AUDIO_OUTPUT_SYNCHRONOUS, 0, espeakNgDataLocation, 0);
        if (result == -1)
        {
            Logger.LogError(
                $"eSpeak failed to initialize from {espeakNgDataLocation}! Not patching!"
            );
            return;
        }
        Logger.LogInfo("eSpeak loaded successfully! Patching Speak Method...");
        var harmony = new Harmony("sane.tongue");
        harmony.PatchAll();
    }

    private static void HandleLanguageChangeEvent(EventData eventData)
    {
        string[] data = (string[])eventData.CustomData;
        string steamID = data[0];
        string language = data[1];

        Instance.languagePerPlayer[steamID] = language;
    }

    // Every time before TTS text is shown, update the current speaker.
    // We do this here because this function gets a reference to the
    // PlayerAvatar of the player speaking currently. Maybe this would've
    // been useful to know before I did all this.
    //
    // We can't do this in HandleLanguageChangeEvent, presumably because
    // it gets called after the first message is already spoken (??)
    // Networked Events are weird like that I suppose
    [HarmonyPatch(typeof(WorldSpaceUIParent), "TTS")]
    [HarmonyPrefix]
    private static void TTSPrefix(PlayerAvatar _player, string _text, float _time)
    {
        string steamID = (string)
            typeof(PlayerAvatar)
                .GetField(
                    "steamID",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
                )
                .GetValue(_player);
        // Logger.LogInfo("Current Speaker: " + (string)steamID);
        Instance.currentSpeaker = steamID;
    }

    public void ChangeTTSLanguageForSteamID(string steamID, string language)
    {
        if (!language_list.Contains(language))
        {
            Logger.LogError(
                $"Language {language} is not valid. Perhaps someone is using a different version of Tongue."
            );
            return;
        }

        PlayerAvatar avatar = SemiFunc.PlayerAvatarGetFromSteamID(steamID);
        if (avatar == null)
        {
            Logger.LogError("Someone wants to change language but provided an invalid steamID!");
            return;
        }
        if (languagePerPlayer.ContainsKey(steamID))
        {
            languagePerPlayer[steamID] = language;
        }
        else
        {
            languagePerPlayer.Add(steamID, language);
        }
    }

    [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.ChatMessageSend))]
    [HarmonyPrefix]
    private static void ChatMessageSendPrefix(PlayerAvatar __instance)
    {
        string steamID = (string)
            typeof(PlayerAvatar)
                .GetField(
                    "steamID",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
                )
                .GetValue(__instance);
        string[] content = { steamID, Instance.languageSetting.Value };
        LanguageChangeEvent.RaiseEvent(
            content,
            NetworkingEvents.RaiseAll,
            SendOptions.SendReliable
        );
    }

    [HarmonyPatch(
        typeof(SpeechSynth),
        nameof(SpeechSynth.speak),
        [typeof(StringBuilder), typeof(int), typeof(SpeechSynth.VoicingSource), typeof(bool)]
    )]
    [HarmonyPrefix]
    private static void SpeakPrefix(
        ref StringBuilder text,
        int voiceFundamentalFreq,
        SpeechSynth.VoicingSource voicingSource,
        ref bool bracketsAsPhonemes
    )
    {
        bracketsAsPhonemes = true;
        string tmp = text.ToString();

        text.Length = 0;
        string language = Instance.languagePerPlayer.GetValueOrDefault(
            Instance.currentSpeaker,
            Instance.languageSetting.Value
        );
        text.Append(phoneticize(tmp, language));

        // Don't mess with regular text
        if (language == "en")
        {
            return;
        }

        // Klattersynth doesn't support the colon as a long indicator, so
        // replace it with another instance of the letter preceding it to
        // emulate the effect.
        while (text.ToString().IndexOf(':') != -1)
        {
            int index = text.ToString().IndexOf(':');
            text[index] = text[index - 1];
        }

        // Some phonemes generated by eSpeak aren't supported by
        // Klattersynth, so we try to approximate them using supported
        // phonemes.
        Dictionary<char, char> phonemeFixes = new Dictionary<char, char>
        {
            { 'C', 'S' },
            { 'Y', '3' },
            { 'E', 'e' },
            { 'a', 'A' },
            { 'W', '3' },
        };

        foreach ((char replacee, char replacement) in phonemeFixes)
        {
            text.Replace(replacee, replacement);
        }

        // eSpeak sometimes tries to change language mid-word like this:
        // [(en)k'u:l(de)]
        // Klattersynth obviously doesn't support this, so we strip out
        // all text in parentheses.
        tmp = Regex.Replace(text.ToString(), @"\([^)]*\)", "").Trim();
        text.Length = 0;
        text.Append(tmp);

        // Logger.LogInfo($"Saying word: {text}");
    }
}
