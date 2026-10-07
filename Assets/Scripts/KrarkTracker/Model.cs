using System;
using System.Collections.Generic;

namespace KrarkTracker
{
    public enum TriggerCondition { CastSpell, CopySpell, Magecraft, WinCoinFlip }

    public enum EffectType
    {
        CopySpell, CreateTreasure, AddRedMana, DealDamage, DrawCard, ReturnSpellToHand, AddCounter,
        /// <summary>Replacement effect: whenever a spell would be copied, copy it one additional time.</summary>
        CopySpellAdditional
    }

    public enum DamageTarget { AnyTarget, TargetPlayer, AllOpponents }

    public enum StackKind { Spell, Copy, Trigger }

    public enum LogKind { Info, Win, Lose, Resource, Damage, Stack }

    public static class Labels
    {
        public static string Condition(TriggerCondition c)
        {
            switch (c)
            {
                case TriggerCondition.CastSpell: return "Cast Spell";
                case TriggerCondition.CopySpell: return "Copy Spell";
                case TriggerCondition.Magecraft: return "Magecraft (cast or copy)";
                default: return "Win a coin flip";
            }
        }

        public static string Effect(EffectType t)
        {
            switch (t)
            {
                case EffectType.CopySpell: return "Copy Spell";
                case EffectType.CreateTreasure: return "Create a Treasure";
                case EffectType.AddRedMana: return "Add Red Mana";
                case EffectType.DealDamage: return "Deal Damage";
                case EffectType.DrawCard: return "Draw a Card";
                case EffectType.ReturnSpellToHand: return "Return Spell to Hand";
                case EffectType.CopySpellAdditional: return "Copy Spell One Additional Time";
                default: return "Add Counter";
            }
        }

        public static string Target(DamageTarget t)
        {
            switch (t)
            {
                case DamageTarget.AnyTarget: return "Any Target";
                case DamageTarget.TargetPlayer: return "Target Player";
                default: return "All Opponents";
            }
        }

        public static List<string> All<T>(Func<T, string> label) where T : Enum
        {
            var list = new List<string>();
            foreach (T v in Enum.GetValues(typeof(T))) list.Add(label(v));
            return list;
        }
    }

    [Serializable]
    public class EffectDef
    {
        public EffectType type;
        public int amount = 1;
        public DamageTarget target;
        public string counterName = "+1/+1 counter";

        public EffectDef() { }
        public EffectDef(EffectType type, int amount = 1) { this.type = type; this.amount = amount; }

        public string Describe()
        {
            int n = Math.Max(1, amount);
            switch (type)
            {
                case EffectType.CopySpell: return n > 1 ? "Copy Spell x" + n : "Copy Spell";
                case EffectType.CreateTreasure: return n > 1 ? "Create " + n + " Treasures" : "Create a Treasure";
                case EffectType.AddRedMana: return "Add " + n + " Red Mana";
                case EffectType.DealDamage: return "Deal " + amount + " damage (" + Labels.Target(target) + ")";
                case EffectType.DrawCard: return n > 1 ? "Draw " + n + " cards" : "Draw a card";
                case EffectType.ReturnSpellToHand: return "Return Spell to Hand";
                case EffectType.CopySpellAdditional: return "Copy Spell One Additional Time (replacement)";
                default: return "Add " + n + " " + counterName;
            }
        }
    }

    [Serializable]
    public class CoinFlipDef
    {
        public int winChance = 50;
        public List<EffectDef> onWin = new List<EffectDef>();
        public List<EffectDef> onLose = new List<EffectDef>();
    }

    [Serializable]
    public class TriggerDef
    {
        public string id;
        public string name;
        public TriggerCondition condition;
        public int quantity;
        public bool builtIn;
        public List<EffectDef> effects = new List<EffectDef>();
        public bool hasCoinFlip;
        public CoinFlipDef coinFlip = new CoinFlipDef();

        public const string KrarkId = "krark";

        /// <summary>
        /// Replacement effects never go on the stack: they modify an event as it happens
        /// (here, a spell being copied) and therefore cannot trigger themselves.
        /// </summary>
        public bool IsReplacement => effects != null && effects.Exists(e => e.type == EffectType.CopySpellAdditional);

        public static TriggerDef CreateKrark()
        {
            var def = new TriggerDef
            {
                id = KrarkId,
                name = "Krark, the Thumbless",
                condition = TriggerCondition.CastSpell,
                quantity = 0,
                builtIn = true,
                hasCoinFlip = true,
            };
            def.coinFlip.winChance = 50;
            def.coinFlip.onWin.Add(new EffectDef(EffectType.CopySpell));
            def.coinFlip.onLose.Add(new EffectDef(EffectType.ReturnSpellToHand));
            return def;
        }

        public List<string> DescribeEffects()
        {
            var lines = new List<string>();
            if (hasCoinFlip)
            {
                lines.Add("Flip a coin (" + coinFlip.winChance + "% to win)");
                lines.Add("  Win: " + JoinEffects(coinFlip.onWin));
                lines.Add("  Lose: " + JoinEffects(coinFlip.onLose));
            }
            foreach (var e in effects) lines.Add(e.Describe());
            return lines;
        }

        public static string JoinEffects(List<EffectDef> list)
        {
            if (list.Count == 0) return "nothing";
            var parts = new List<string>();
            foreach (var e in list) parts.Add(e.Describe());
            return string.Join(", ", parts);
        }
    }

    [Serializable]
    public class SaveData
    {
        public List<TriggerDef> board = new List<TriggerDef>();
        public int treasure, redMana, storm;
        public bool krarksThumb;
    }
}
