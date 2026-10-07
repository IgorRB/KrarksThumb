using System;
using System.Collections.Generic;
using UnityEngine;

namespace KrarkTracker
{
    public class StackItem
    {
        public int id;
        public StackKind kind;
        public string title;
        public string source;
        public List<string> lines = new List<string>();
        public TriggerDef def;
        public int spellId;
        public string spellTitle;
    }

    public class LogEntry
    {
        public string text;
        public LogKind kind;
    }

    public enum TriggerEvent { Cast, Copy, FlipWin }

    /// <summary>The outcome of one resolved Krark coin flip, used for on-screen feedback.</summary>
    public class FlipResult
    {
        public string source;   // e.g. "Krark, the Thumbless (2/3)"
        public bool win;
        public string effects;  // what happens because of the result
        public string note;     // extra context, e.g. what the ignored Krark's Thumb coin was
    }

    public class SummaryRow
    {
        public string label, value;
        public bool header;
    }

    /// <summary>Everything that happened while the whole stack was resolved automatically.</summary>
    public class ResolveSummary
    {
        public int startStack, objects, triggers, spells, copiesResolved;
        public int flipsWon, flipsLost, thumbFlips, ignoredWins, ignoredLosses;
        public int copies, additionalCopies, spellsReturned, copiesReturned, returnFizzled;
        public int treasure, redMana, damage, cards;
        public int treasureTotal, redManaTotal;
        public bool stackLimitHit;
        public readonly Dictionary<string, int> counters = new Dictionary<string, int>();
        public readonly Dictionary<string, int> byTrigger = new Dictionary<string, int>();

        public List<SummaryRow> Rows()
        {
            var rows = new List<SummaryRow>();
            Action<string> head = t => rows.Add(new SummaryRow { label = t, header = true });
            Action<string, object> row = (l, v) => rows.Add(new SummaryRow { label = l, value = v.ToString() });

            head("Stack");
            row("Started with", startStack + " object(s)");
            row("Resolved", objects + " (" + triggers + " triggers, " + spells + " spells, " + copiesResolved + " copies)");
            if (stackLimitHit) row("Warning", "stack limit reached, extra objects discarded");

            if (flipsWon + flipsLost > 0)
            {
                head("Coin flips");
                row("Won", flipsWon);
                row("Lost", flipsLost);
                if (thumbFlips > 0)
                    row("Krark's Thumb auto-picks", thumbFlips + " (ignored: " + ignoredWins + " wins, " + ignoredLosses + " losses)");
            }

            if (copies + spellsReturned + copiesReturned + returnFizzled > 0)
            {
                head("Spells");
                if (copies > 0) row("Copies created", copies);
                if (additionalCopies > 0) row("  from replacement effects", additionalCopies);
                if (spellsReturned > 0) row("Spells returned to hand", spellsReturned);
                if (copiesReturned > 0) row("Copies returned (ceased to exist)", copiesReturned);
                if (returnFizzled > 0) row("Return effects with no target", returnFizzled);
            }

            if (treasure + redMana + damage + cards > 0 || counters.Count > 0)
            {
                head("Resources and effects");
                if (treasure > 0) row("Treasures", "+" + treasure + " (now " + treasureTotal + ")");
                if (redMana > 0) row("Red mana", "+" + redMana + " (now " + redManaTotal + ")");
                if (damage > 0) row("Damage dealt", damage);
                if (cards > 0) row("Cards drawn", cards);
                foreach (var kv in counters) row(kv.Key, "+" + kv.Value);
            }

            if (byTrigger.Count > 0)
            {
                head("Triggers resolved");
                foreach (var kv in byTrigger) row(kv.Key, "x" + kv.Value);
            }
            return rows;
        }
    }

    /// <summary>A Krark trigger waiting for the player to pick one of the two Krark's Thumb flips.</summary>
    public class PendingFlip
    {
        public StackItem item;
        public bool[] results;
    }

    /// <summary>Pure game logic: board state, stack, resources and effect resolution.</summary>
    public class GameEngine
    {
        public const int MaxStack = 400;
        public const int MaxLog = 60;
        const string PrefsKey = "krark.tracker.save";

        public readonly List<TriggerDef> board = new List<TriggerDef>();
        /// <summary>Last element is the top of the stack.</summary>
        public readonly List<StackItem> stack = new List<StackItem>();
        /// <summary>Newest entry first.</summary>
        public readonly List<LogEntry> log = new List<LogEntry>();

        public int treasure, redMana, storm;
        public int damageDealt, cardsDrawn;
        /// <summary>Krark's Thumb: flip two coins and ignore one.</summary>
        public bool krarksThumb;
        public PendingFlip Pending { get; private set; }
        public readonly Dictionary<string, int> counters = new Dictionary<string, int>();

        public event Action Changed;
        /// <summary>Raised when a Krark flip resolves one by one. Not raised during ResolveAll (the summary covers it).</summary>
        public event Action<FlipResult> CoinFlipResolved;
        string thumbNote;

        int nextId = 1;
        int spellCount;
        /// <summary>Non-null only while ResolveAll is running.</summary>
        ResolveSummary tally;
        /// <summary>Suppresses change notifications during ResolveAll so the UI refreshes once at the end.</summary>
        bool quiet;
        readonly System.Random rng;

        public GameEngine(int? seed = null)
        {
            rng = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
            board.Add(TriggerDef.CreateKrark());
        }

        public StackItem Top => stack.Count > 0 ? stack[stack.Count - 1] : null;

        // ------------------------------------------------------------ Board

        public TriggerDef Krark => board.Find(t => t.id == TriggerDef.KrarkId);

        public TriggerDef AddTrigger(string name, TriggerCondition condition, List<EffectDef> effects)
        {
            // A replacement effect stands alone and only ever applies to a spell being copied.
            if (effects.Exists(e => e.type == EffectType.CopySpellAdditional))
            {
                condition = TriggerCondition.CopySpell;
                effects = new List<EffectDef> { new EffectDef(EffectType.CopySpellAdditional) };
            }

            var def = new TriggerDef
            {
                id = Guid.NewGuid().ToString("N"),
                name = string.IsNullOrWhiteSpace(name) ? "New trigger" : name.Trim(),
                condition = condition,
                quantity = 1,
                effects = effects,
            };
            board.Add(def);
            Notify();
            return def;
        }

        public void ChangeQuantity(TriggerDef def, int delta)
        {
            def.quantity = Mathf.Clamp(def.quantity + delta, 0, 99);
            Notify();
        }

        public void RemoveTrigger(TriggerDef def)
        {
            if (def.builtIn) return;
            board.Remove(def);
            Notify();
        }

        // ------------------------------------------------------- Resources

        public void AddTreasure(int d) { treasure = Math.Max(0, treasure + d); Notify(); }
        public void AddRedMana(int d) { redMana = Math.Max(0, redMana + d); Notify(); }
        public void AddStorm(int d) { storm = Math.Max(0, storm + d); Notify(); }

        public void SetKrarksThumb(bool on)
        {
            krarksThumb = on;
            Notify();
        }

        public void ResetResources()
        {
            // Treasures are persistent (they stay around between turns), so they are not reset.
            redMana = storm = damageDealt = cardsDrawn = 0;
            counters.Clear();
            stack.Clear();
            Pending = null;
            spellCount = 0;
            Say("Resources and stack reset (Treasures kept).", LogKind.Info);
            Notify();
        }

        // ----------------------------------------------------------- Stack

        public void CastSpell()
        {
            if (Pending != null) return;
            storm++;
            spellCount++;
            var spell = new StackItem
            {
                id = nextId++,
                kind = StackKind.Spell,
                title = "Spell #" + spellCount,
                source = "Cast",
            };
            spell.spellId = spell.id;
            spell.spellTitle = spell.title;
            spell.lines.Add("Spell cast. Storm count is now " + storm + ".");
            stack.Add(spell);
            Say(spell.title + " cast (storm count " + storm + ").", LogKind.Stack);
            PushAll(CollectTriggers(TriggerEvent.Cast, spell.id, spell.title));
            Notify();
        }

        public void ResolveTop()
        {
            var item = Top;
            if (item == null || Pending != null) return;
            stack.RemoveAt(stack.Count - 1);

            if (tally != null)
            {
                tally.objects++;
                if (item.kind == StackKind.Trigger)
                {
                    tally.triggers++;
                    tally.byTrigger.TryGetValue(item.def.name, out int c);
                    tally.byTrigger[item.def.name] = c + 1;
                }
                else if (item.kind == StackKind.Spell) tally.spells++;
                else tally.copiesResolved++;
            }

            var pending = new List<StackItem>();
            switch (item.kind)
            {
                case StackKind.Spell:
                    Say(item.title + " resolves.", LogKind.Stack);
                    break;
                case StackKind.Copy:
                    Say(item.title + " resolves (copy ceases to exist).", LogKind.Stack);
                    break;
                default:
                    Say("Resolving " + item.title + ".", LogKind.Info);
                    ResolveTrigger(item, pending);
                    break;
            }
            PushAll(pending);
            Notify();
        }

        /// <summary>
        /// Counters a chosen spell, copy or triggered ability: it leaves the stack without resolving, so none
        /// of its effects happen. Objects above and below it are untouched (a countered spell's triggers stay).
        /// </summary>
        public void CounterTarget(StackItem target)
        {
            if (target == null || Pending != null) return;
            if (!stack.Remove(target)) return;
            Say(target.title + " was countered (removed without resolving).", LogKind.Stack);
            Notify();
        }

        /// <summary>
        /// Resolves everything on the stack, top first, until it is empty. With Krark's Thumb active the
        /// coin is picked automatically: first aim for at least one win, then at least one loss, and once
        /// both were kept, always pick a win when one is available. Returns null if there was nothing to do.
        /// </summary>
        public ResolveSummary ResolveAll()
        {
            if (Pending != null || stack.Count == 0) return null;

            var summary = new ResolveSummary { startStack = stack.Count };
            tally = summary;
            quiet = true;
            try
            {
                int guard = 0;
                while ((stack.Count > 0 || Pending != null) && guard++ < 20000)
                {
                    if (Pending != null) ChooseFlip(AutoPick(Pending), true);
                    else ResolveTop();
                }
            }
            finally
            {
                tally = null;
                quiet = false;
            }

            summary.treasureTotal = treasure;
            summary.redManaTotal = redMana;
            Say("Resolved the whole stack (" + summary.objects + " objects).", LogKind.Stack);
            Notify();
            return summary;
        }

        int AutoPick(PendingFlip p)
        {
            int win = Array.IndexOf(p.results, true);
            int lose = Array.IndexOf(p.results, false);
            if (tally.flipsWon == 0 && win >= 0) return win;     // 1st priority: at least one success
            if (tally.flipsLost == 0 && lose >= 0) return lose;  // 2nd priority: at least one failure
            return win >= 0 ? win : lose;                        // then success whenever available
        }

        public void ClearStack()
        {
            if (stack.Count == 0 || Pending != null) return;
            Say("Stack cleared (" + stack.Count + " objects removed).", LogKind.Stack);
            stack.Clear();
            Notify();
        }

        void PushAll(List<StackItem> items)
        {
            foreach (var i in items)
            {
                if (stack.Count >= MaxStack)
                {
                    if (tally != null) tally.stackLimitHit = true;
                    Say("Stack limit reached (" + MaxStack + "); extra objects discarded.", LogKind.Lose);
                    return;
                }
                stack.Add(i);
            }
        }

        // -------------------------------------------------------- Triggers

        static bool Matches(TriggerCondition c, TriggerEvent e)
        {
            switch (c)
            {
                case TriggerCondition.CastSpell: return e == TriggerEvent.Cast;
                case TriggerCondition.CopySpell: return e == TriggerEvent.Copy;
                case TriggerCondition.Magecraft: return e == TriggerEvent.Cast || e == TriggerEvent.Copy;
                default: return e == TriggerEvent.FlipWin;
            }
        }

        List<StackItem> CollectTriggers(TriggerEvent ev, int spellId, string spellTitle)
        {
            var result = new List<StackItem>();
            foreach (var def in board)
            {
                // Replacement effects are applied while copying, never as stack triggers.
                if (def.quantity <= 0 || def.IsReplacement || !Matches(def.condition, ev)) continue;
                for (int i = 1; i <= def.quantity; i++)
                {
                    var item = new StackItem
                    {
                        id = nextId++,
                        kind = StackKind.Trigger,
                        title = def.quantity > 1 ? def.name + " (" + i + "/" + def.quantity + ")" : def.name,
                        source = Labels.Condition(def.condition),
                        def = def,
                        spellId = spellId,
                        spellTitle = spellTitle,
                    };
                    item.lines.AddRange(def.DescribeEffects());
                    result.Add(item);
                }
            }
            return result;
        }

        bool FlipCoin(CoinFlipDef flip) { return rng.NextDouble() * 100.0 < flip.winChance; }

        static string Outcome(bool win) { return win ? "WIN" : "LOSE"; }

        void ResolveTrigger(StackItem item, List<StackItem> pending)
        {
            var def = item.def;
            if (!def.hasCoinFlip)
            {
                FinishTrigger(item, null, pending);
                return;
            }

            bool first = FlipCoin(def.coinFlip);
            if (!krarksThumb)
            {
                FinishTrigger(item, first, pending);
                return;
            }

            // Krark's Thumb: flip two coins; the player chooses which result to keep.
            bool second = FlipCoin(def.coinFlip);
            Pending = new PendingFlip { item = item, results = new[] { first, second } };
            Say(def.name + " + Krark's Thumb: flipped " + Outcome(first) + " and " + Outcome(second) + ". Choose one.", LogKind.Info);
        }

        /// <summary>Resolves the Krark trigger that was waiting on Krark's Thumb with the chosen coin (0 or 1).</summary>
        public void ChooseFlip(int index, bool auto = false)
        {
            var p = Pending;
            if (p == null || index < 0 || index >= p.results.Length) return;
            Pending = null;

            if (tally != null)
            {
                tally.thumbFlips++;
                if (p.results[1 - index]) tally.ignoredWins++; else tally.ignoredLosses++;
            }
            Say((auto ? "Auto-picked coin " : "Kept coin ") + (index + 1) + " (" + Outcome(p.results[index]) + "); the other is ignored.", LogKind.Info);
            thumbNote = "Krark's Thumb: the other coin was " + Outcome(p.results[1 - index]);
            var pending = new List<StackItem>();
            FinishTrigger(p.item, p.results[index], pending);
            PushAll(pending);
            Notify();
        }

        void FinishTrigger(StackItem item, bool? flip, List<StackItem> pending)
        {
            var def = item.def;
            if (flip.HasValue)
            {
                bool win = flip.Value;
                if (tally != null) { if (win) tally.flipsWon++; else tally.flipsLost++; }
                Say(def.name + ": coin flip - " + Outcome(win) + ".", win ? LogKind.Win : LogKind.Lose);
                if (tally == null)
                {
                    CoinFlipResolved?.Invoke(new FlipResult
                    {
                        source = item.title,
                        win = win,
                        effects = TriggerDef.JoinEffects(win ? def.coinFlip.onWin : def.coinFlip.onLose),
                        note = thumbNote,
                    });
                }
                thumbNote = null;
                foreach (var e in win ? def.coinFlip.onWin : def.coinFlip.onLose)
                    Apply(e, item, pending);
                if (win)
                    pending.AddRange(CollectTriggers(TriggerEvent.FlipWin, item.spellId, item.spellTitle));
            }
            foreach (var e in def.effects) Apply(e, item, pending);
        }

        /// <summary>How many extra copies replacement effects on the board add to a single copy event.</summary>
        int AdditionalCopies()
        {
            int total = 0;
            foreach (var def in board)
                if (def.IsReplacement && def.quantity > 0) total += def.quantity;
            return total;
        }

        void Apply(EffectDef e, StackItem source, List<StackItem> pending)
        {
            int n = Math.Max(1, e.amount);
            switch (e.type)
            {
                case EffectType.CopySpell:
                    // Replacement effects modify this copy event once. The extra copies are part of the
                    // same event, so they are not run through the replacement again (no loop possible).
                    int extra = AdditionalCopies();
                    if (extra > 0)
                        Say("Replacement effect: " + source.spellTitle + " is copied " + extra + " additional time(s).", LogKind.Info);

                    for (int i = 0; i < n + extra; i++)
                    {
                        bool additional = i >= n;
                        string title = (additional ? "Additional copy of " : "Copy of ") + source.spellTitle;
                        var copy = new StackItem
                        {
                            id = nextId++,
                            kind = StackKind.Copy,
                            title = title,
                            source = "Copy",
                            spellTitle = title,
                        };
                        copy.spellId = copy.id;
                        copy.lines.Add(additional
                            ? "Extra copy from a replacement effect."
                            : "A copy of " + source.spellTitle + " was put on the stack.");
                        pending.Add(copy);
                        if (tally != null) { tally.copies++; if (additional) tally.additionalCopies++; }
                        Say(copy.title + " created.", LogKind.Win);
                        pending.AddRange(CollectTriggers(TriggerEvent.Copy, copy.id, copy.title));
                    }
                    break;

                case EffectType.CopySpellAdditional:
                    break; // replacement effect: handled by AdditionalCopies(), never resolved from the stack

                case EffectType.CreateTreasure:
                    treasure += n;
                    if (tally != null) tally.treasure += n;
                    Say("+" + n + " Treasure (total " + treasure + ").", LogKind.Resource);
                    break;

                case EffectType.AddRedMana:
                    redMana += n;
                    if (tally != null) tally.redMana += n;
                    Say("+" + n + " red mana (total " + redMana + ").", LogKind.Resource);
                    break;

                case EffectType.DealDamage:
                    damageDealt += e.amount;
                    if (tally != null) tally.damage += e.amount;
                    Say(e.amount + " damage dealt (" + Labels.Target(e.target) + "). Total " + damageDealt + ".", LogKind.Damage);
                    break;

                case EffectType.DrawCard:
                    cardsDrawn += n;
                    if (tally != null) tally.cards += n;
                    Say("Drew " + n + " card(s) (total " + cardsDrawn + ").", LogKind.Resource);
                    break;

                case EffectType.ReturnSpellToHand:
                    int idx = stack.FindIndex(s => s.id == source.spellId && s.kind != StackKind.Trigger);
                    if (idx < 0)
                    {
                        if (tally != null) tally.returnFizzled++;
                        Say(source.spellTitle + " is no longer on the stack; nothing returns.", LogKind.Info);
                    }
                    else
                    {
                        var removed = stack[idx];
                        stack.RemoveAt(idx);
                        if (tally != null) { if (removed.kind == StackKind.Copy) tally.copiesReturned++; else tally.spellsReturned++; }
                        Say(removed.kind == StackKind.Copy
                            ? removed.title + " returned to hand and ceases to exist."
                            : removed.title + " returned to hand.", LogKind.Lose);
                    }
                    break;

                case EffectType.AddCounter:
                    counters.TryGetValue(e.counterName, out int cur);
                    counters[e.counterName] = cur + n;
                    if (tally != null)
                    {
                        tally.counters.TryGetValue(e.counterName, out int tc);
                        tally.counters[e.counterName] = tc + n;
                    }
                    Say("+" + n + " " + e.counterName + " (total " + counters[e.counterName] + ").", LogKind.Resource);
                    break;
            }
        }

        // ------------------------------------------------------------- Log

        void Say(string text, LogKind kind)
        {
            log.Insert(0, new LogEntry { text = text, kind = kind });
            if (log.Count > MaxLog) log.RemoveAt(log.Count - 1);
        }

        void Notify() { if (!quiet) Changed?.Invoke(); }

        // ----------------------------------------------------- Persistence

        public void Save()
        {
            var data = new SaveData { board = board, treasure = treasure, redMana = redMana, storm = storm, krarksThumb = krarksThumb };
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(data));
            PlayerPrefs.Save();
        }

        public void Load()
        {
            if (!PlayerPrefs.HasKey(PrefsKey)) return;
            SaveData data;
            try { data = JsonUtility.FromJson<SaveData>(PlayerPrefs.GetString(PrefsKey)); }
            catch (Exception) { return; }
            if (data == null) return;

            treasure = data.treasure;
            redMana = data.redMana;
            storm = data.storm;
            krarksThumb = data.krarksThumb;

            board.Clear();
            var krark = TriggerDef.CreateKrark();
            var savedKrark = data.board?.Find(t => t.id == TriggerDef.KrarkId);
            if (savedKrark != null) krark.quantity = Mathf.Clamp(savedKrark.quantity, 0, 99);
            board.Add(krark);
            if (data.board != null)
                foreach (var t in data.board)
                    if (t.id != TriggerDef.KrarkId && !t.builtIn && t.effects != null) board.Add(t);
        }
    }
}
