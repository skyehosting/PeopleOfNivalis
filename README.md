# People of Nivalis

A mod for **Nivalis Nights**: walk up to an ordinary person, press **T**, and talk to them in your own words.

Everyone you meet gets a lasting identity, a mood, a job and a life that moves on between meetings, and they
remember you: your name if you gave it, what you talked about, and how they feel about you. People talk among
themselves too (on the phone, at restaurant tables, muttering to themselves), and lines can be spoken aloud with
voices that fade with distance. Story characters remember the scenes you've had with them, without spoilers.

**[Download the latest release](https://github.com/skyehosting/PeopleOfNivalis/releases/latest)** ·
**[Website](https://skyehosting.github.io/PeopleOfNivalis/)**

## What it does

- **Talk to anyone.** Look at a person, press **T**, type. They stop what they're doing, turn to you, and answer in
  character, knowing their job, their district, the time, the weather and the city's news.
- **People remember.** Each person keeps their own memory of you, a diary of your meetings, and a feeling about you
  that moves with every conversation (shown as a bar in the chat). Old memories fade the way real ones do.
- **The city talks.** Residents chat with each other, take phone calls, gossip about you, overhear things, and talk
  through their orders with the staff at restaurants.
- **Voices.** Lines can be spoken with OpenAI, ElevenLabs, Azure, Google, Deepgram, a local server, or Windows'
  own voices, in 3D, with a tone that follows the conversation.
- **A phone.** Press **N**: text the people whose number you have, read the news, and use Nightline, the city's
  social app, where residents post, react and comment. A camera and a gallery too.
- **Your rules.** Your own instructions for every character, a usage limit for what the AI and voices may cost,
  and a switch for every feature.
- Characters under 18 are never chatted with.

## What you need

- Nivalis Nights, with BepInEx for IL2CPP games (its "bleeding edge" build) installed in the game folder.
- An AI to talk with: a key from a provider (OpenAI, Anthropic, Google Gemini, OpenRouter, Groq, DeepSeek and
  others), or a model running on your own PC (Ollama, LM Studio, KoboldCpp...).
- Optional: a voice provider, and a Voyage AI key so people can recall old memories by meaning.
- Recommended: Nivalis Switchboard (a separate download) for in-game settings. Without it, every setting is in a
  text file.

## Install

1. Close the game.
2. Open the game folder (the one with `Nivalis Nights.exe` in it; in Steam: right-click the game > Manage >
   Browse local files).
3. Download `PeopleOfNivalis-<version>.zip` from [Releases](https://github.com/skyehosting/PeopleOfNivalis/releases/latest)
   and drag its `BepInEx` folder into the game folder. You should end up with
   `BepInEx\plugins\PeopleOfNivalis\NivalisAIChat.dll`.
4. Start the game once. The mod writes its settings file, `BepInEx\config\PeopleOfNivalis.cfg`.

Updating: do the same with the new zip. Your settings and everyone's memories are kept.

## Set up your AI

**With Nivalis Switchboard:** press **F9**, open People of Nivalis, then the Setup Wizard at the top:
1. Your AI: pick a provider (it fills in the address and models), paste your key, and press Test connection.
2. Voices (optional): turn on a provider, add its key, and press Test voices.
3. Your usage: set the most the mod may use per hour, day or month, and use the plan it recommends.

**Without Switchboard:** open `BepInEx\config\PeopleOfNivalis.cfg` in a text editor. At the top, set `ApiFormat`,
`ApiUrl`, `ApiKey` and `Model` (the file lists the common providers), and save. While you're in the game world, a
saved change applies within a couple of seconds.

A cheap model that works well: OpenAI's `gpt-5.4-nano` (`ApiFormat = OpenAI`, `ApiUrl = https://api.openai.com/v1`).

## Play

- Look at a person. When **[T] Talk** appears, press **T**. Type, then **Enter**. Press **Esc** to leave; they go
  back to what they were doing.
- Story characters keep their own dialogue on the game's talk key; **T** opens a chat with them under their real name.
- Press **Enter** outside a chat to say something to the people in front of you; pick who you're talking to (or All).
- Ask a friend to go fishing with you, or ask by text or on Nightline for someone to come keep you company.
- The **`** key (left of 1) opens the console. `/help` lists every command, `/errors` says what went wrong (with
  a code), `/usage` shows what's been used against your limits, and `/heard` shows the lines you missed.

## Your keys and costs

- Your keys stay in `BepInEx\config\PeopleOfNivalis.cfg` on your PC and are sent only to the provider you set them
  for. The mod never writes them to its log. **Don't share that file**, or `BepInEx\config\NivalisAIChat\ai_profiles.json`.
- AI providers and most voice providers charge for what you use. Set a usage limit (Setup Wizard, step 3, or
  `TokenLimit` and `VoiceLimit` in the file). Once a limit is reached, nothing more is asked until it starts again.
- Voices are on to start: with an OpenAI key, people speak with OpenAI's voices, paid from the same key. To turn
  them off, set `Enabled = false` under `[Voices]`.

## Where things are

- Settings: `BepInEx\config\PeopleOfNivalis.cfg`
- Everyone's memories, and backups: `BepInEx\config\NivalisAIChat\`. In a chat, `/resetnpc` makes that person
  forget you; `/resetall` makes everyone forget you (type it twice).
- Your own instructions for every character: `BepInEx\config\NivalisAIChat\custom_instructions.txt`
- If something goes wrong: `/errors` in the console, and `BepInEx\LogOutput.log` (it never holds your keys).

To uninstall, delete `BepInEx\plugins\PeopleOfNivalis`. Delete `BepInEx\config\NivalisAIChat` and
`PeopleOfNivalis.cfg` too to remove your settings, keys and memories.

## Reporting a problem

Open an [issue](https://github.com/skyehosting/PeopleOfNivalis/issues) with what you did, what you saw, and
`BepInEx\LogOutput.log` from your game folder. Never attach `PeopleOfNivalis.cfg` or `ai_profiles.json`: they hold
your keys.

## Building from source

You need a recent .NET SDK and the game with BepInEx installed (the build references the game's
`BepInEx\core` and `BepInEx\interop`, which BepInEx generates on the game's first start).

```
dotnet build NivalisAIChat.csproj -c Release
```

The DLL is `bin\Release\net6.0\NivalisAIChat.dll`. If the game isn't in Steam's default folder, add
`-p:GameDir="D:\path\to\Nivalis Nights"`. Add `-p:DeployToGame=true` to copy the DLL into the game's plugins
folder (close the game first).

The tests need no game: `cd tests` then `dotnet run -c Release`. Keep them green, and give new behaviour a test.
Other mods can talk to this one through its plugin API: see [docs/PLUGINS.md](docs/PLUGINS.md).

## Contributing

Forks and pull requests are welcome. Please keep a pull request to one change, say what it fixes or adds, and run
the tests first.

## Licence

People of Nivalis is © skyehosting, licensed under
[Creative Commons Attribution-NonCommercial-ShareAlike 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)
(see [LICENSE](LICENSE)):

- **Credit:** say where it came from and link back here.
- **No selling:** no commercial use, including paid downloads, paywalls or early access for money.
- **Share alike:** anything you make from it must be shared under this same licence.

Nivalis Nights and its content belong to their owners; this mod is unofficial and contains none of the game's files.
