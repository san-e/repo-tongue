using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Strobotnik.Klattersynth;

namespace tongue;

[BepInPlugin("sane.tongue", "Tongue", "0.0.1")]
[BepInProcess("REPO.exe")]
[BepInIncompatibility("Lavighju.espeakTTS")]
[HarmonyPatch]
public class Tongue : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    public static Tongue Instance { get; private set; }
    private ConfigEntry<string> languageSetting;
    const int AUDIO_OUTPUT_SYNCHRONOUS = 0;
    const int espeakCHARS_UTF8 = 1;
    const int PHONEME_MODE_ASCII = 0;

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

    public static string phoneticize(string text)
    {
        if (Instance.languageSetting.Value == "en")
        {
            return text;
        }
        espeak_SetVoiceByName(Instance.languageSetting.Value);

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
        Logger = base.Logger;
        languageSetting = Config.Bind(
            "General",
            "Language",
            "de",
            new ConfigDescription("", new AcceptableValueList<string>(language_list))
        );
        string espeakNgDataLocation = Directory.GetFiles(Paths.PluginPath, "phondata-manifest", SearchOption.AllDirectories)[0].Replace("phondata-manifest", "");
        Logger.LogInfo("Attempting to load eSpeak data from " + espeakNgDataLocation);
        int result = espeak_Initialize(AUDIO_OUTPUT_SYNCHRONOUS, 0, espeakNgDataLocation, 0);
        if (result == -1) {
            Logger.LogError($"eSpeak failed to initialize from {espeakNgDataLocation}! Not patching!");
            return;
        }
        Logger.LogInfo("eSpeak loaded successfully! Patching Strobotnik.Klattersynth.SpeechSynth.speak()...");
        var harmony = new Harmony("sane.tongue");
        harmony.PatchAll();
    }

    static MethodBase TargetMethod()
    {
        return typeof(SpeechSynth).GetMethod(
            "speak",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[]
            {
                typeof(StringBuilder),
                typeof(int),
                typeof(SpeechSynth.VoicingSource),
                typeof(bool),
            },
            null
        );
    }

    static void Prefix(
        ref StringBuilder text,
        int voiceFundamentalFreq,
        SpeechSynth.VoicingSource voicingSource,
        ref bool bracketsAsPhonemes
    )
    {
        bracketsAsPhonemes = true;
        string tmp = text.ToString();
        text.Length = 0;
        text.Append(phoneticize(tmp));

        while (text.ToString().IndexOf(':') != -1)
        {
            int index = text.ToString().IndexOf(':');
            text[index] = text[index - 1];
        }
        ;

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
    }
}
