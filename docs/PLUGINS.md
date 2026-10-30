# People of Nivalis plugins

People of Nivalis lets other mods add to its conversations. A plugin can:

- **add context**: one short line about the world goes into the prompt of whoever the player is talking to;
- **add reply tags**: the AI can add a hidden note such as `[[gift: a red scarf]]`, and your code gets the value;
- **filter lines**: rewrite what someone says to the player before it is voiced and shown;
- **listen to events**: a chat opened or closed, the player's words, a line said to the player, the relationship moving,
  a reminder coming due.

The API is `NivalisAIChat.Api.PonApi`, version `PonApi.Version` (1). Its names stay the same in the public build, so a
plugin compiled against one release keeps working until the version number changes.

## Two ways to make a plugin

**1. A DLL in the plugins folder (simplest).** Put it in
`BepInEx\plugins\PeopleOfNivalis\plugins\`. People of Nivalis loads it when the game starts. Write a public class that
implements `IPeopleOfNivalisPlugin` and has a public constructor with no parameters:

```csharp
using NivalisAIChat.Api;

public sealed class WeatherMoods : IPeopleOfNivalisPlugin
{
    public string Id => "weather-moods";      // short, unique, no spaces
    public string Name => "Weather moods";
    public string Version => "1.0.0";

    public void Load()
    {
        PonApi.RegisterContext(Id, req => $"It's been raining all day in {req.Location}; everyone's a bit damp.");
    }

    public void Unload() { }
}
```

**2. Your own BepInEx plugin.** Add `[BepInDependency("PeopleOfNivalis")]` to your plugin class and call `PonApi` from
your `Load()`. Use your plugin's GUID as the `pluginId`.

Either way, reference `NivalisAIChat.dll` (from `BepInEx\plugins\PeopleOfNivalis\`) with `Private=false`, so you don't
ship a copy of it. Target `net6.0`. `docs/plugins/ExamplePlugin` has a complete project.

## The calls

All calls take your plugin's id first. Everything you register under that id is released together when the plugin
is switched off or unloaded.

| Call | What it does |
|---|---|
| `RegisterContext(id, Func<ContextRequest, string?>)` | Returns one line (300 characters at most) or `null`. It is called on the game's thread each time a chat prompt is built, so keep it quick. At most 4 lines from all plugins go into a prompt, first registered first. |
| `RegisterReplyTag(id, tag, instruction, Action<NpcInfo, string>)` | `tag` is 2 to 24 characters, `a-z` and `_` only. The mod's own tags (mood, follow, romance, reminder…) are refused. `instruction` tells the AI when to use the tag, for example "when you give them something (what you give)." Your handler runs on the game's thread after the line is shown. It returns false if the tag is taken. |
| `RegisterLineFilter(id, Func<NpcInfo, string, string?>)` | Returns new words, or `null`/`""` to keep the line. It runs on a **worker thread**, before the line is voiced, so don't touch Unity objects there. The person's own memory keeps the AI's original words. |
| `On(id, subscribe, unsubscribe)` | Subscribes to an event so it is released with your plugin: `PonApi.On(Id, () => PonApi.ChatOpened += Opened, () => PonApi.ChatOpened -= Opened);` |
| `Log(id, message)` | Writes a line to the BepInEx log, marked with your id. |

The events are `ChatOpened(NpcInfo)`, `ChatClosed(NpcInfo)`, `PlayerSaid(NpcInfo, string)`, `NpcSaid(NpcInfo, string)`,
`RelationshipChanged(NpcInfo, before, after)` (0 to 100) and `ReminderDue(string)`. They fire on the game's thread. Subscribe there too, from `Load()`.

`NpcInfo` is a read-only snapshot with these fields:

- `Id`: stable within a save.
- `Name`: the name the player knows them by; it reads "Stranger" until they give it.
- `Relationship`: 0 to 100, where 50 means a stranger.
- `Mood`.
- `Romance`: `""`, `dating` or `living`.
- `TimesMet`.
- `Occupation`: as they told it.

## The rules

- **Your code runs inside the game, like any mod.** People of Nivalis catches every exception from a plugin and logs
  it. After 3 errors in a row, the plugin is switched off until the game restarts. This counts errors from its event
  handlers too. A switched-off plugin gets its `Unload()`, and its handlers are taken off the events.
- **No one under 18, ever.** People of Nivalis never opens a chat with anyone under 18 and never sends them to an AI, so
  your plugin never hears about them. Don't use the API to build such a chat yourself.
- **No keys.** Nothing in the API carries the player's API keys, and a plugin must never ask for them or log them.
- **Keep lines short and factual.** Context lines are background facts for the AI. They are not instructions to it.
  Square brackets are replaced, so a line can't pose as one of the prompt's own sections.

## Switching plugins off

In People of Nivalis's settings (the in-game menu or Switchboard, under **Plugins**):

- `LoadPlugins` (on by default): load the folder's plugins at start.
- `DisabledPlugins`: ids or file names to leave out, separated by commas.

Only add plugins you trust. A plugin can do anything a BepInEx mod can.
