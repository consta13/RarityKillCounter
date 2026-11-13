using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ExileCore.Shared.Static;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace RarityKillCounter
{
    public class RarityKillCounter : BaseSettingsPlugin<RarityKillCounterSettings>
    {
        private const int MaxZoneHistory = 5;
        private const string InvulnerableBuffName = "cannot_be_damaged";

        private string CachePath => Path.Combine(DirectoryFullName, "ZoneCache.json");
        private string IgnoreListPath => Path.Combine(DirectoryFullName, "IgnoredUniquePaths.txt");
        private string UniqueLogPath => Path.Combine(DirectoryFullName, "UniqueKillLog.txt");

        private List<ZoneRecord> _cache = new List<ZoneRecord>();
        private HashSet<long> _counted = new HashSet<long>();
        private uint? _currentHash;
        private int _white;
        private int _magic;
        private int _rare;
        private int _unique;
        private bool _dirty;

        private double _accumulatedSeconds;
        private DateTime _zoneEnteredUtc = DateTime.UtcNow;
        private double CurrentElapsedSeconds => _accumulatedSeconds + (DateTime.UtcNow - _zoneEnteredUtc).TotalSeconds;

        private PathIgnoreList _ignoredUniquePaths = PathIgnoreList.Parse(new string[0]);
        private readonly Dictionary<long, UniqueObservation> _uniqueObs = new Dictionary<long, UniqueObservation>();

        public override bool Initialise()
        {
            _cache = LoadCache();
            _ignoredUniquePaths = LoadIgnoreList();
            return true;
        }

        // Fires on every zone transition, including re-entering the SAME instance
        // through your own portal - which is exactly when this used to wipe counts
        // (and now the timer) for a zone that's still actively "in progress". Banks
        // the outgoing zone's final elapsed time before switching, then looks the new
        // hash up in the on-disk cache instead of always zeroing: a hit resumes the
        // counters, the dedup set, AND the timer's accumulated time; a genuine new
        // zone starts everything at zero, same as before.
        public override void AreaChange(AreaInstance area)
        {
            if (_currentHash.HasValue)
                SaveCurrentZone();

            _currentHash = area.Hash;
            var record = _cache.FirstOrDefault(r => r.Hash == _currentHash);

            if (record != null)
            {
                _white = record.White;
                _magic = record.Magic;
                _rare = record.Rare;
                _unique = record.Unique;
                _counted = new HashSet<long>(record.CountedIds ?? new List<long>());
                _accumulatedSeconds = record.ElapsedSeconds;
            }
            else
            {
                _white = 0;
                _magic = 0;
                _rare = 0;
                _unique = 0;
                _counted = new HashSet<long>();
                _accumulatedSeconds = 0;
            }

            _zoneEnteredUtc = DateTime.UtcNow;
            _uniqueObs.Clear();
            _ignoredUniquePaths = LoadIgnoreList(); // picks up edits to the ignore file on the next zone
            _dirty = false;
        }

        // Dead monsters stay in ValidEntitiesByType for a while after death, so this
        // sees the same corpse across many ticks - _counted is what stops it double-counting.
        // Unique kills are always tallied regardless of the display toggle below, so
        // flipping Settings.CountUnique mid-zone never loses a count.
        public override Job Tick()
        {
            foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
            {
                if (entity.IsAlive)
                {
                    if (Settings.DebugLogUniques) ObserveAliveUnique(entity);
                    continue;
                }

                if (!entity.IsHostile) continue;
                if (!entity.HasComponent<ObjectMagicProperties>()) continue;
                if (!_counted.Add(entity.Id)) continue;

                var rarity = entity.Rarity;

                // Some game effects spawn as Unique-rarity monsters that cannot be killed
                // (Ultimatum flame skulls etc.) and "die" when the game despawns them.
                // A dead Unique only counts if it doesn't look like one of those.
                if (rarity == MonsterRarity.Unique)
                {
                    var rejectReason = UniqueRejectReason(entity);
                    if (Settings.DebugLogUniques) LogUniqueDecision(entity, rejectReason);
                    _uniqueObs.Remove(entity.Id);

                    if (rejectReason != null)
                    {
                        _dirty = true; // stays in _counted so it isn't re-judged after a rejoin
                        continue;
                    }
                }

                switch (rarity)
                {
                    case MonsterRarity.White:
                        _white++;
                        break;
                    case MonsterRarity.Magic:
                        _magic++;
                        break;
                    case MonsterRarity.Rare:
                        _rare++;
                        break;
                    case MonsterRarity.Unique:
                        _unique++;
                        break;
                }

                _dirty = true;
            }

            // Write-through on the tick a kill actually happens, not every tick -
            // survives a crash/alt-f4 without hammering disk on every frame. The
            // timer rides along on the same save (one extra field, effectively free),
            // so its on-disk value also refreshes whenever a kill happens to occur.
            if (_dirty)
            {
                SaveCurrentZone();
                _dirty = false;
            }

            return null;
        }

        public override void Render()
        {
            if (!Settings.Enable) return;
            if (GameController.Area.CurrentArea == null) return;
            if (!Settings.ShowInTown && (GameController.Area.CurrentArea.IsTown || GameController.Area.CurrentArea.IsHideout))
                return;

            var position = new Vector2(Settings.PositionX, Settings.PositionY);

            if (Settings.ShowTimer && Settings.TimerAboveCounts)
                position = DrawLine(FormatElapsed(CurrentElapsedSeconds), position, Settings.TimerColor);

            position = DrawLine($"White: {_white}", position, Settings.WhiteColor);
            position = DrawLine($"Magic: {_magic}", position, Settings.MagicColor);
            position = DrawLine($"Rare: {_rare}", position, Settings.RareColor);

            if (Settings.CountUnique)
                position = DrawLine($"Unique: {_unique}", position, Settings.UniqueColor);

            if (Settings.ShowTimer && !Settings.TimerAboveCounts)
                DrawLine(FormatElapsed(CurrentElapsedSeconds), position, Settings.TimerColor);
        }

        private Vector2 DrawLine(string text, Vector2 position, Color color)
        {
            var lineSize = Graphics.DrawText(text, position, color);
            position.Y += lineSize.Y;
            return position;
        }

        // MM:SS only, no hour component - minutes just keep climbing past 59
        // (e.g. 90:23) instead of rolling over, per how this is meant to display.
        private static string FormatElapsed(double totalSecondsRaw)
        {
            var totalSeconds = (int)totalSecondsRaw;
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return $"{minutes:D2}:{seconds:D2}";
        }

        // ---- Unique "is this a real kill" guard -------------------------------------

        // Returns null when a dead Unique looks like a genuine kill, otherwise the reason
        // it was rejected. Four independent signals, all things a killable monster never has:
        //   - path matches an entry in IgnoredUniquePaths.txt (you control this one)
        //   - DiesAfterTime component: timed-lifetime effect/summon that expires on its own
        //   - BaseCannotBeDamaged stat: innately invulnerable
        //   - "cannot_be_damaged" buff: invulnerable via status effect
        // A monster that is actually killed cannot be invulnerable at the moment it dies,
        // so none of these fire for a real boss kill, even one with invulnerable phases.
        private string UniqueRejectReason(Entity entity)
        {
            if (_ignoredUniquePaths.Matches(entity.Path)) return "ignored-path";
            if (entity.HasComponent<DiesAfterTime>()) return "timed-lifetime";
            if (HasStat(entity, GameStat.BaseCannotBeDamaged)) return "cannot-be-damaged-stat";
            if (HasInvulnerableBuff(entity)) return "cannot-be-damaged-buff";
            return null;
        }

        private static bool HasStat(Entity entity, GameStat stat)
        {
            var stats = entity.Stats;
            return stats != null && stats.TryGetValue(stat, out var value) && value != 0;
        }

        private static bool HasInvulnerableBuff(Entity entity)
        {
            var buffs = entity.GetComponent<Buffs>();
            return buffs != null && buffs.HasBuff(InvulnerableBuffName);
        }

        // Debug only: remembers the lowest HP% each Unique was seen at while alive. A real
        // boss loses HP before it dies; an invulnerable effect sits at full HP until it
        // is despawned - that difference is the clearest evidence in the log.
        private void ObserveAliveUnique(Entity entity)
        {
            if (!entity.HasComponent<ObjectMagicProperties>()) return;
            if (entity.Rarity != MonsterRarity.Unique) return;
            if (!entity.IsHostile) return;

            var life = entity.GetComponent<Life>();
            var hpPercent = life != null ? Convert.ToDouble(life.HPPercentage) : double.NaN;

            if (!_uniqueObs.TryGetValue(entity.Id, out var obs))
                _uniqueObs[entity.Id] = new UniqueObservation { FirstSeenUtc = DateTime.UtcNow, MinHpPercent = hpPercent };
            else if (double.IsNaN(obs.MinHpPercent) || hpPercent < obs.MinHpPercent)
                obs.MinHpPercent = hpPercent;
        }

        private void LogUniqueDecision(Entity entity, string rejectReason)
        {
            try
            {
                _uniqueObs.TryGetValue(entity.Id, out var obs);
                var life = entity.GetComponent<Life>();

                var line = string.Join(" | ",
                    DateTime.Now.ToString("HH:mm:ss"),
                    rejectReason == null ? "COUNTED" : "IGNORED:" + rejectReason,
                    "path=" + entity.Path,
                    "targetable=" + entity.IsTargetable,
                    "diesAfterTime=" + entity.HasComponent<DiesAfterTime>(),
                    "cannotBeDamagedStat=" + HasStat(entity, GameStat.BaseCannotBeDamaged),
                    "cannotBeDamagedBuff=" + HasInvulnerableBuff(entity),
                    "hpAtDeath=" + (life != null ? life.CurHP + "/" + life.MaxHP : "n/a"),
                    "minHpPercentSeenAlive=" + (obs != null ? obs.MinHpPercent.ToString("0.##") : "n/a"),
                    "secondsSeenAlive=" + (obs != null ? (DateTime.UtcNow - obs.FirstSeenUtc).TotalSeconds.ToString("0.0") : "n/a"));

                File.AppendAllText(UniqueLogPath, line + Environment.NewLine);
            }
            catch
            {
                // Debug logging must never take the plugin down.
            }
        }

        private PathIgnoreList LoadIgnoreList()
        {
            try
            {
                if (!File.Exists(IgnoreListPath))
                    File.WriteAllText(IgnoreListPath, PathIgnoreList.DefaultFileText);
                return PathIgnoreList.Parse(File.ReadAllLines(IgnoreListPath));
            }
            catch
            {
                return PathIgnoreList.Parse(new string[0]);
            }
        }

        // ---- Zone persistence -------------------------------------------------------

        // Updates (or inserts) this zone's record and evicts the least-recently-active
        // entry once more than MaxZoneHistory distinct hashes are on file. LRU rather
        // than strict insertion order: a zone you keep portaling back into should never
        // get pushed out just because you also touched a few other zones in between.
        private void SaveCurrentZone()
        {
            if (!_currentHash.HasValue) return;
            var hash = _currentHash.Value;

            var record = _cache.FirstOrDefault(r => r.Hash == hash);
            if (record == null)
            {
                record = new ZoneRecord { Hash = hash };
                _cache.Add(record);
            }

            record.White = _white;
            record.Magic = _magic;
            record.Rare = _rare;
            record.Unique = _unique;
            record.CountedIds = _counted.ToList();
            record.ElapsedSeconds = CurrentElapsedSeconds;
            record.LastSeenUtcTicks = DateTime.UtcNow.Ticks;

            if (_cache.Count > MaxZoneHistory)
            {
                var oldest = _cache.OrderBy(r => r.LastSeenUtcTicks).First();
                _cache.Remove(oldest);
            }

            WriteCache();
        }

        private List<ZoneRecord> LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return new List<ZoneRecord>();
                var json = File.ReadAllText(CachePath);
                return JsonConvert.DeserializeObject<List<ZoneRecord>>(json) ?? new List<ZoneRecord>();
            }
            catch
            {
                // Missing or corrupt cache file - start fresh rather than crash the plugin.
                return new List<ZoneRecord>();
            }
        }

        private void WriteCache()
        {
            try
            {
                File.WriteAllText(CachePath, JsonConvert.SerializeObject(_cache, Formatting.Indented));
            }
            catch
            {
                // Disk write failed - worst case this zone's progress doesn't survive
                // a rejoin, which is exactly the old behavior. Not fatal.
            }
        }

        private class ZoneRecord
        {
            public uint Hash { get; set; }
            public int White { get; set; }
            public int Magic { get; set; }
            public int Rare { get; set; }
            public int Unique { get; set; }
            public List<long> CountedIds { get; set; } = new List<long>();
            public double ElapsedSeconds { get; set; }
            public long LastSeenUtcTicks { get; set; }
        }

        private class UniqueObservation
        {
            public DateTime FirstSeenUtc { get; set; }
            public double MinHpPercent { get; set; }
        }
    }

    // BEGIN PathIgnoreList
    // Case-insensitive substring matcher over entity paths, loaded from a plain text file:
    // one entry per line, blank lines and lines starting with # are ignored.
    internal sealed class PathIgnoreList
    {
        public static readonly string DefaultFileText = string.Join(Environment.NewLine,
            "# Uniques whose entity path contains any entry below are NOT counted as kills.",
            "# One entry per line, case-insensitive substring match. Lines starting with # are comments.",
            "# To find a path: DevTree > entity > Path, or enable \"Log Unique Decisions\" in the plugin",
            "# settings and read UniqueKillLog.txt. Edits are picked up on the next zone change.",
            "Monsters/Daemon/",
            "FlamespitterDaemon",
            "");

        private readonly List<string> _patterns;

        private PathIgnoreList(List<string> patterns)
        {
            _patterns = patterns;
        }

        public static PathIgnoreList Parse(IEnumerable<string> lines)
        {
            var patterns = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw == null ? "" : raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                patterns.Add(line);
            }
            return new PathIgnoreList(patterns);
        }

        public bool Matches(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (var pattern in _patterns)
                if (path.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
    // END PathIgnoreList

    public class RarityKillCounterSettings : ISettings
    {
        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        [Menu("Show In Town / Hideout")]
        public ToggleNode ShowInTown { get; set; } = new ToggleNode(false);

        [Menu("Count Unique Kills")]
        public ToggleNode CountUnique { get; set; } = new ToggleNode(true);

        [Menu("White Text Color")]
        public ColorNode WhiteColor { get; set; } = Color.White;

        [Menu("Magic Text Color")]
        public ColorNode MagicColor { get; set; } = HudSkin.MagicColor;

        [Menu("Rare Text Color")]
        public ColorNode RareColor { get; set; } = HudSkin.RareColor;

        [Menu("Unique Text Color")]
        public ColorNode UniqueColor { get; set; } = HudSkin.UniqueColor;

        [Menu("Position X")]
        public RangeNode<int> PositionX { get; set; } = new RangeNode<int>(100, 0, 5760);

        [Menu("Position Y")]
        public RangeNode<int> PositionY { get; set; } = new RangeNode<int>(300, 0, 2160);

        [Menu("Show Timer")]
        public ToggleNode ShowTimer { get; set; } = new ToggleNode(true);

        [Menu("Timer Above Monster Counts")]
        public ToggleNode TimerAboveCounts { get; set; } = new ToggleNode(true);

        [Menu("Timer Text Color")]
        public ColorNode TimerColor { get; set; } = Color.White;

        [Menu("Log Unique Decisions (debug)")]
        public ToggleNode DebugLogUniques { get; set; } = new ToggleNode(false);
    }
}
