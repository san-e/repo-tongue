# Tongue
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/san-e/repo-tongue)  
**Tongue** is a plugin that forces the Semibots to speak in a language of your choosing!

## How?
Under the hood, Tongue uses the powerful [eSpeak NG](https://github.com/espeak-ng/espeak-ng) speech synthesis project to **phoneticize** chat messages. Instead of feeding raw text into the TTS, eSpeak NG converts it into a string of phonemes that mimic how the selected language would pronounce it. This results in bots "speaking" with different accents or entirely different linguistic patterns.

eSpeak NG supports over 100 languages and dialects. I've only tested German, so I can't promise all of them will sound perfect—or even intelligible—but the variety is there for experimentation and chaos. Want your Semibots to mumble Icelandic or shout in Swahili? Go ahead. They won’t stop you.

