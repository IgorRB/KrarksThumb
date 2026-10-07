using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace KrarkTracker
{
    /// <summary>Builds the whole UI Toolkit interface in code and binds it to a GameEngine.</summary>
    [RequireComponent(typeof(UIDocument))]
    public class KrarkApp : MonoBehaviour
    {
        GameEngine engine;
        public GameEngine Engine => engine;

        VisualElement playView, boardView;
        Button tabPlay, tabBoard;

        ScrollView stackScroll, logScroll, boardScroll, draftScroll;
        Button castBtn, resolveBtn, counterBtn, clearBtn;
        Label stackHint;
        /// <summary>True after pressing "Counter target": the next tap on a stack object counters it.</summary>
        bool counterMode;
        const string DefaultStackHint = "Tap Resolve: top object. Hold 3s: whole stack.";
        const string CounterStackHint = "Counter: tap the spell or trigger to remove.";
        VisualElement thumbOverlay, resultsOverlay, holdFill;
        VisualElement thumbBanner, thumbSwitch, toastLayer;
        Label thumbBadge;

        /// <summary>How long the Resolve button must be held to resolve the whole stack.</summary>
        public const float HoldSeconds = 3f;
        IVisualElementScheduledItem holdItem;
        float holdStart;
        bool holdFired;
        Label treasureValue, redValue, stormValue, statsLabel;
        Label formHint, replacementHint;
        Button addEffectBtn;
        TextField nameField;
        DropdownField condField;

        readonly List<EffectDef> draft = new List<EffectDef>();

        // Floating input: on touch devices the virtual keyboard would cover the form fields, so tapping a
        // field opens a centered input above the keyboard instead of editing the field in place.
        /// <summary>Editor-only helper: pretend the keyboard covers this fraction (0..1) of the screen height.</summary>
        public static float SimulatedKeyboardFraction;
        bool floatingEnabled;
        VisualElement inputOverlay;
        Label inputTitle;
        TextField inputField;
        Action<string> inputCommit;
        bool inputNumeric;
        IVisualElementScheduledItem inputKeyboardTick;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<KrarkApp>() != null) return;

            var go = new GameObject("KrarkApp");
            var doc = go.AddComponent<UIDocument>();
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1280, 720);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("KrarkRuntimeTheme");
            doc.panelSettings = panel;
            go.AddComponent<KrarkApp>();
        }

        void Start()
        {
            engine = new GameEngine();
            engine.Load();
            draft.Add(new EffectDef(EffectType.CopySpell));
            floatingEnabled = Application.isMobilePlatform;

            BuildUI(GetComponent<UIDocument>().rootVisualElement);
            engine.Changed += OnEngineChanged;
            engine.CoinFlipResolved += ShowFlipToast;
            OnEngineChanged();
        }

        void OnDestroy()
        {
            if (engine == null) return;
            engine.Changed -= OnEngineChanged;
            engine.CoinFlipResolved -= ShowFlipToast;
        }

        void OnApplicationQuit() { engine?.Save(); }
        void OnApplicationPause(bool paused) { if (paused) engine?.Save(); }

        void OnEngineChanged()
        {
            counterMode = false; // any change to the game cancels a pending "counter target" selection
            RefreshStack();
            RefreshResources();
            RefreshLog();
            RefreshBoard();
            RefreshOverlay();
            UpdateThumbSwitch();
            engine.Save();
        }

        // ------------------------------------------------------------------ Layout

        static VisualElement El(string cls = null, params VisualElement[] children)
        {
            var e = new VisualElement();
            if (cls != null) foreach (var c in cls.Split(' ')) e.AddToClassList(c);
            foreach (var c in children) e.Add(c);
            return e;
        }

        static Label Lbl(string text, string cls = null)
        {
            var l = new Label(text);
            if (cls != null) foreach (var c in cls.Split(' ')) l.AddToClassList(c);
            return l;
        }

        static Button Btn(string text, string cls, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            if (cls != null) foreach (var c in cls.Split(' ')) b.AddToClassList(c);
            return b;
        }

        void BuildUI(VisualElement root)
        {
            root.Clear();
            root.style.flexGrow = 1;
            var app = El("app");
            app.styleSheets.Add(Resources.Load<StyleSheet>("KrarkTheme"));
            app.RegisterCallback<GeometryChangedEvent>(e =>
            {
                app.EnableInClassList("portrait", e.newRect.width < e.newRect.height || e.newRect.width < 900);
                ApplySafeArea(app);
            });
            ApplyAppFont(app);
            root.Add(app);

            // Header + tabs
            tabPlay = Btn("Play", "tab active", () => ShowTab(true));
            tabBoard = Btn("Board", "tab", () => ShowTab(false));
            app.Add(El("header", Lbl(Application.productName, "title"), El("tabs", tabPlay, tabBoard)));
            app.Add(El("header-rule"));

            playView = BuildPlayView();
            boardView = BuildBoardView();
            boardView.AddToClassList("hidden");
            app.Add(playView);
            app.Add(boardView);

            // Floating balloon used to choose a coin when Krark's Thumb is active. Added last so it sits on top.
            thumbOverlay = El("overlay hidden");
            app.Add(thumbOverlay);

            resultsOverlay = El("overlay hidden");
            app.Add(resultsOverlay);

            // Floating result texts never take touches, so they sit above everything except the text input card.
            toastLayer = El("toast-layer");
            toastLayer.pickingMode = PickingMode.Ignore;
            app.Add(toastLayer);

            BuildInputOverlay(app);
        }

        // ------------------------------------------------------ Floating result toast

        /// <summary>Editor/testing helper: stretches the toast animation (1 = normal speed).</summary>
        public static float ToastTimeScale = 1f;
        /// <summary>Total time a result text stays on screen, from first frame to removal.</summary>
        const float ToastLifetime = 1.8f;
        const float ToastFadeIn = 0.2f, ToastFadeOut = 0.45f;
        /// <summary>The text starts this far below its resting spot and ends this far above it, at constant speed.</summary>
        const float ToastSlideFrom = 18f, ToastSlideTo = -18f;
        const int MaxToasts = 3;

        /// <summary>
        /// Shows the outcome of a Krark flip as floating text. It lives for <see cref="ToastLifetime"/> seconds:
        /// it fades in, stays fully opaque, then fades out, while sliding up at a constant speed the whole time.
        /// </summary>
        void ShowFlipToast(FlipResult r)
        {
            var toast = El("toast " + (r.win ? "toast-win" : "toast-lose"),
                Lbl(r.source, "toast-source"),
                Lbl(r.win ? "WIN" : "LOSE", "toast-result"),
                Lbl(r.effects, "toast-effects"));
            if (!string.IsNullOrEmpty(r.note)) toast.Add(Lbl(r.note, "toast-note"));

            // Toasts are decoration only: nothing in them may swallow a tap meant for the app underneath.
            toast.pickingMode = PickingMode.Ignore;
            toast.Query<VisualElement>().ForEach(e => e.pickingMode = PickingMode.Ignore);

            toast.style.opacity = 0f;
            toast.style.translate = new Translate(0f, ToastSlideFrom);
            toastLayer.Add(toast);
            while (toastLayer.childCount > MaxToasts) toastLayer.RemoveAt(0);

            float lifetime = ToastLifetime * ToastTimeScale;
            float fadeIn = ToastFadeIn * ToastTimeScale, fadeOut = ToastFadeOut * ToastTimeScale;
            float start = Time.unscaledTime;
            IVisualElementScheduledItem tick = null;
            tick = toast.schedule.Execute(() =>
            {
                float t = Time.unscaledTime - start;
                if (t >= lifetime)
                {
                    tick.Pause();
                    toast.RemoveFromHierarchy();
                    return;
                }

                // Opacity: fade in, stay at 100%, fade out. Slide: one linear motion over the whole lifetime.
                float alpha = Mathf.Min(Mathf.Clamp01(t / fadeIn), Mathf.Clamp01((lifetime - t) / fadeOut));
                toast.style.opacity = alpha;
                toast.style.translate = new Translate(0f, Mathf.Lerp(ToastSlideFrom, ToastSlideTo, t / lifetime));
            }).Every(16);
        }

        // ------------------------------------------------------- Floating input

        void BuildInputOverlay(VisualElement app)
        {
            inputOverlay = El("overlay input-overlay hidden");
            // Tapping the dimmed background (not the card) confirms and closes.
            inputOverlay.RegisterCallback<PointerDownEvent>(e => { if (e.target == inputOverlay) CloseInput(); });

            inputTitle = Lbl("", "balloon-title");
            inputField = new TextField();
            inputField.AddToClassList("big-input");
            inputField.RegisterValueChangedCallback(ev =>
            {
                string text = FilterInput(ev.newValue);
                if (text != ev.newValue) inputField.SetValueWithoutNotify(text);
                inputCommit?.Invoke(text);
            });
            inputField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) CloseInput();
            });

            inputOverlay.Add(El("balloon input-balloon", inputTitle, inputField, Btn("Done", "btn-ok", CloseInput)));
            app.Add(inputOverlay);
        }

        /// <summary>Turns the floating input on or off (it is on by default on mobile platforms).</summary>
        public void SetFloatingInput(bool on)
        {
            floatingEnabled = on;
            nameField.isReadOnly = on;
            RebuildDraft();
        }

        /// <summary>Makes a form field open the floating input when tapped, instead of editing in place.</summary>
        void MakeFloating<T>(TextInputBaseField<T> field, string title, Func<string> get, Action<string> set, TouchScreenKeyboardType keyboard)
        {
            field.isReadOnly = floatingEnabled;
            field.RegisterCallback<ClickEvent>(e =>
            {
                if (!floatingEnabled) return;
                e.StopPropagation();
                OpenInput(title, get(), set, keyboard);
            }, TrickleDown.TrickleDown);
        }

        /// <summary>A virtual (on-screen) keyboard that the app opens, reads and closes itself.</summary>
        public interface IVirtualKeyboard
        {
            string Text { get; set; }
            /// <summary>False once the user pressed Done/Cancel or the keyboard lost focus.</summary>
            bool IsOpen { get; }
            void Close();
        }

        sealed class NativeKeyboard : IVirtualKeyboard
        {
            TouchScreenKeyboard keyboard;

            public NativeKeyboard(string text, TouchScreenKeyboardType type)
            {
                // Our floating card shows the text, so the keyboard must not draw its own input bar on top of it.
                TouchScreenKeyboard.hideInput = true;
                keyboard = TouchScreenKeyboard.Open(text, type, false, false, false);
            }

            public string Text
            {
                get => keyboard != null ? keyboard.text : "";
                set { if (keyboard != null) keyboard.text = value; }
            }

            public bool IsOpen => keyboard != null && keyboard.status == TouchScreenKeyboard.Status.Visible;

            public void Close()
            {
                if (keyboard == null) return;
                keyboard.active = false; // this is what actually dismisses the keyboard on Android and iOS
                keyboard = null;
            }
        }

        /// <summary>
        /// Creates the virtual keyboard used by the floating input. Defaults to the platform keyboard on touch
        /// devices; when null on desktop the floating input is a normal text field. Can be replaced in tests.
        /// </summary>
        public static Func<string, TouchScreenKeyboardType, IVirtualKeyboard> KeyboardFactory;

        static bool CanUseNativeKeyboard => Application.isMobilePlatform && TouchScreenKeyboard.isSupported;

        IVirtualKeyboard virtualKeyboard;

        void OpenInput(string title, string value, Action<string> commit, TouchScreenKeyboardType keyboard)
        {
            CloseKeyboard();
            inputCommit = null; // don't write the initial value back
            inputNumeric = keyboard == TouchScreenKeyboardType.NumberPad;
            inputTitle.text = title;
            inputField.keyboardType = keyboard;
            inputField.SetValueWithoutNotify(value);
            inputCommit = commit;

            // On touch devices the app drives the keyboard itself (so Done can really close it) and the card only
            // mirrors the text. On desktop the card's field is a regular editable text field.
            var factory = KeyboardFactory ?? (CanUseNativeKeyboard ? (Func<string, TouchScreenKeyboardType, IVirtualKeyboard>)((t, k) => new NativeKeyboard(t, k)) : null);
            virtualKeyboard = factory?.Invoke(value, keyboard);
            inputField.isReadOnly = virtualKeyboard != null;

            inputOverlay.RemoveFromClassList("hidden");
            UpdateKeyboardInset();
            inputKeyboardTick?.Pause();
            inputKeyboardTick = inputOverlay.schedule.Execute(PollInput).Every(60);
            if (virtualKeyboard == null) inputField.schedule.Execute(() => inputField.Focus()).ExecuteLater(60);
        }

        /// <summary>Runs while the floating input is open: keeps it above the keyboard and mirrors what is typed.</summary>
        public void PollInput()
        {
            UpdateKeyboardInset();
            if (virtualKeyboard == null) return;

            string typed = virtualKeyboard.Text;
            if (typed != inputField.value)
            {
                inputField.value = typed; // also forwards the text to the real field via inputCommit
                if (inputField.value != typed) virtualKeyboard.Text = inputField.value; // e.g. non-digits removed
            }

            // The user pressed the keyboard's own Done/Cancel (or it was dismissed): close the card as well.
            if (!virtualKeyboard.IsOpen) CloseInput();
        }

        /// <summary>Numeric inputs only keep digits.</summary>
        string FilterInput(string text)
        {
            if (!inputNumeric || string.IsNullOrEmpty(text)) return text ?? "";
            var digits = new StringBuilder();
            foreach (char c in text) if (char.IsDigit(c)) digits.Append(c);
            return digits.ToString();
        }

        void CloseKeyboard()
        {
            virtualKeyboard?.Close();
            virtualKeyboard = null;
        }

        void CloseInput()
        {
            // Read what was typed before the keyboard goes away, so the last characters are not lost. The text is
            // written straight to the form field: a ChangeEvent raised from inside this button click would only be
            // processed after the click handler returns, by which time inputCommit is already cleared.
            if (virtualKeyboard != null)
            {
                string pending = FilterInput(virtualKeyboard.Text);
                if (pending != inputField.value)
                {
                    inputField.SetValueWithoutNotify(pending);
                    inputCommit?.Invoke(pending);
                }
            }
            CloseKeyboard();
            inputKeyboardTick?.Pause();
            inputKeyboardTick = null;
            inputCommit = null;
            inputField.Blur();
            inputOverlay.AddToClassList("hidden");
        }

        /// <summary>Keeps the floating card centered in the part of the screen the keyboard does not cover.</summary>
        void UpdateKeyboardInset()
        {
            float panelHeight = inputOverlay.panel != null ? inputOverlay.panel.visualTree.layout.height : 0f;
            if (panelHeight <= 0f || Screen.height <= 0) return;

            float inset = 0f;
            if (TouchScreenKeyboard.isSupported && TouchScreenKeyboard.visible)
                inset = TouchScreenKeyboard.area.height * (panelHeight / Screen.height);
            if (inset <= 0f && Application.isMobilePlatform)
                inset = panelHeight * 0.5f; // keyboard size not reported (yet): assume it covers the bottom half
            if (inset <= 0f) inset = panelHeight * SimulatedKeyboardFraction;

            inputOverlay.style.paddingBottom = inset;
        }

        // ---------------------------------------------------- Hold to resolve all

        void OnResolveClicked()
        {
            // A completed hold already resolved everything; swallow the click fired when the button is released.
            if (holdFired) { holdFired = false; return; }
            engine.ResolveTop();
        }

        /// <summary>Starts the 3 second press. Normally called by the pointer-down handler of the Resolve button.</summary>
        public void BeginHold()
        {
            if (!resolveBtn.enabledSelf) return;
            holdFired = false;
            holdStart = Time.unscaledTime;
            holdItem?.Pause();
            holdItem = resolveBtn.schedule.Execute(HoldTick).Every(16);
        }

        void HoldTick()
        {
            float t = (Time.unscaledTime - holdStart) / HoldSeconds;
            holdFill.style.width = Length.Percent(Mathf.Clamp01(t) * 100f);
            if (t < 1f) return;

            CancelHold();
            holdFired = true;
            ResolveAllNow();
        }

        void EndHold()
        {
            CancelHold();
            // Forget the "hold completed" flag once the release click has been processed.
            resolveBtn.schedule.Execute(() => holdFired = false).ExecuteLater(80);
        }

        void CancelHold()
        {
            holdItem?.Pause();
            holdItem = null;
            holdFill.style.width = Length.Percent(0);
        }

        /// <summary>Resolves the whole stack and shows the results pop-up.</summary>
        public void ResolveAllNow()
        {
            var summary = engine.ResolveAll();
            if (summary != null) ShowSummary(summary);
        }

        void ShowSummary(ResolveSummary summary)
        {
            resultsOverlay.Clear();
            var balloon = El("balloon results");
            balloon.Add(Lbl("Stack resolved", "balloon-title"));

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("results-scroll");
            foreach (var r in summary.Rows())
            {
                if (r.header) scroll.Add(Lbl(r.label, "sum-head"));
                else scroll.Add(El("sum-row", Lbl(r.label, "sum-label"), Lbl(r.value, "sum-value")));
            }
            balloon.Add(scroll);
            balloon.Add(Btn("Close", "btn-ok", () => resultsOverlay.AddToClassList("hidden")));
            resultsOverlay.Add(balloon);
            resultsOverlay.RemoveFromClassList("hidden");
        }

        void RefreshOverlay()
        {
            var p = engine.Pending;
            thumbOverlay.EnableInClassList("hidden", p == null);
            thumbOverlay.Clear();
            if (p == null) return;

            var def = p.item.def;
            var balloon = El("balloon");
            balloon.Add(Lbl("Krark's Thumb", "balloon-title"));
            balloon.Add(Lbl(p.item.title + " flipped two coins. Choose the result to keep; the other is ignored.", "balloon-text"));

            var coins = El("row coins");
            for (int i = 0; i < p.results.Length; i++)
            {
                int idx = i;
                bool win = p.results[i];
                var effects = TriggerDef.JoinEffects(win ? def.coinFlip.onWin : def.coinFlip.onLose);
                var btn = Btn("", "coin-btn " + (win ? "coin-win" : "coin-lose"), () => engine.ChooseFlip(idx));
                btn.Add(Lbl("Coin " + (i + 1), "coin-name"));
                btn.Add(Lbl(win ? "WIN" : "LOSE", "coin-result"));
                btn.Add(Lbl(effects, "coin-effects"));
                coins.Add(btn);
            }
            balloon.Add(coins);
            thumbOverlay.Add(balloon);
        }

        /// <summary>Pads the app by the device safe area (notches, rounded corners, gesture bars) on top of the theme margins.</summary>
        static void ApplySafeArea(VisualElement app)
        {
            var panel = app.panel;
            if (panel == null || Screen.height <= 0) return;

            float scale = panel.visualTree.layout.height / Screen.height;
            var safe = Screen.safeArea;
            float top = Mathf.Max(0f, Screen.height - safe.yMax) * scale;
            float bottom = Mathf.Max(0f, safe.yMin) * scale;
            float left = Mathf.Max(0f, safe.xMin) * scale;
            float right = Mathf.Max(0f, Screen.width - safe.xMax) * scale;

            // The USS padding (6/14/30/14) is the base; the safe area only ever adds to it.
            app.style.paddingTop = 6f + top;
            app.style.paddingBottom = 30f + bottom;
            app.style.paddingLeft = 14f + left;
            app.style.paddingRight = 14f + right;
        }

        /// <summary>Uses a serif system font (Palatino/Georgia/...) for a more storybook, card-like look when one exists.</summary>
        static void ApplyAppFont(VisualElement app)
        {
            try
            {
                var font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Palatino Linotype", "Book Antiqua", "Georgia", "Cambria", "Times New Roman", "Noto Serif", "Serif" }, 16);
                if (font != null) app.style.unityFont = font;
            }
            catch (Exception) { /* keep the default font */ }
        }

        public void ShowTab(bool play)
        {
            playView.EnableInClassList("hidden", !play);
            boardView.EnableInClassList("hidden", play);
            tabPlay.EnableInClassList("active", play);
            tabBoard.EnableInClassList("active", !play);
        }

        // -------------------------------------------------------------------- Play

        VisualElement BuildPlayView()
        {
            var view = El("view");

            // Stack column
            var stackPanel = El("panel col-main");
            castBtn = Btn("Cast Spell", "btn-cast", () => engine.CastSpell());
            thumbBadge = Lbl("Krark's Thumb", "badge thumb");
            stackHint = Lbl(DefaultStackHint, "stack-hint");
            // Slim info line: title, short hint and the Thumb badge; the buttons below share one row with Cast Spell.
            stackPanel.Add(El("stack-info", Lbl("Stack", "panel-title stack-title"), stackHint, thumbBadge));

            resolveBtn = Btn("Resolve top", "btn-ok", OnResolveClicked);
            // The progress fill is a sibling of the button (a child would make Unity ignore the button's text size).
            holdFill = new VisualElement { pickingMode = PickingMode.Ignore };
            holdFill.AddToClassList("hold-fill");
            var resolveWrap = El("hold-wrap", resolveBtn, holdFill);
            resolveBtn.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) BeginHold(); }, TrickleDown.TrickleDown);
            resolveBtn.RegisterCallback<PointerUpEvent>(_ => EndHold(), TrickleDown.TrickleDown);
            resolveBtn.RegisterCallback<PointerCancelEvent>(_ => EndHold());
            resolveBtn.RegisterCallback<PointerCaptureOutEvent>(_ => CancelHold());
            counterBtn = Btn("Counter target", "btn-warn btn-compact", ToggleCounterMode);
            clearBtn = Btn("Clear stack", "btn-danger btn-compact", () => engine.ClearStack());
            resolveWrap.AddToClassList("btn-compact");
            stackPanel.Add(El("stack-controls", castBtn, resolveWrap, counterBtn, clearBtn));

            stackScroll = new ScrollView(ScrollViewMode.Vertical);
            stackScroll.AddToClassList("fill");
            stackPanel.Add(stackScroll);
            view.Add(stackPanel);

            // Side column: resources + log
            var side = El("col-side");

            var res = El("panel res-panel");
            res.Add(Lbl("Resources", "panel-title"));
            res.Add(ResourceRow("Treasures", "res-treasure", "pip-treasure", out treasureValue,
                () => engine.AddTreasure(-1), () => engine.AddTreasure(1)));
            res.Add(ResourceRow("Red mana", "res-red", "pip-red", out redValue,
                () => engine.AddRedMana(-1), () => engine.AddRedMana(1)));
            res.Add(ResourceRow("Storm count", "res-storm", "pip-storm", out stormValue,
                () => engine.AddStorm(-1), () => engine.AddStorm(1)));
            statsLabel = Lbl("", "stats");
            res.Add(statsLabel);
            res.Add(Btn("Reset resources & stack", "btn-small btn-danger", () => engine.ResetResources()));
            side.Add(res);

            var logPanel = El("panel");
            logPanel.Add(Lbl("Log", "panel-title"));
            logScroll = new ScrollView(ScrollViewMode.Vertical);
            logScroll.AddToClassList("fill");
            logPanel.Add(logScroll);
            side.Add(logPanel);

            view.Add(side);
            return view;
        }

        VisualElement ResourceRow(string label, string valueClass, string pipClass, out Label value, Action minus, Action plus)
        {
            value = Lbl("0", "res-value " + valueClass);
            var name = El("row res-label", El("pip " + pipClass), Lbl(label));
            return El("res-row",
                name,
                Btn("-", "btn-round", minus),
                value,
                Btn("+", "btn-round", plus));
        }

        void RefreshResources()
        {
            treasureValue.text = engine.treasure.ToString();
            redValue.text = engine.redMana.ToString();
            stormValue.text = engine.storm.ToString();

            var sb = new StringBuilder();
            sb.Append("Damage dealt: ").Append(engine.damageDealt)
              .Append("   Cards drawn: ").Append(engine.cardsDrawn);
            foreach (var kv in engine.counters) sb.Append("   ").Append(kv.Key).Append(": ").Append(kv.Value);
            statsLabel.text = sb.ToString();
        }

        /// <summary>"Counter target": arm (or cancel) the selection; the next tap on a stack object counters it.</summary>
        public void ToggleCounterMode()
        {
            if (engine.stack.Count == 0 || engine.Pending != null) return;
            counterMode = !counterMode;
            RefreshStack();
        }

        void RefreshStack()
        {
            bool waiting = engine.Pending != null;
            bool any = engine.stack.Count > 0;
            castBtn.SetEnabled(!waiting);
            resolveBtn.SetEnabled(any && !waiting);
            counterBtn.SetEnabled(any && !waiting);
            clearBtn.SetEnabled(any && !waiting);

            if (!any || waiting) counterMode = false;
            counterBtn.text = counterMode ? "Cancel counter" : "Counter target";
            counterBtn.EnableInClassList("btn-active", counterMode);
            stackHint.text = counterMode ? CounterStackHint : DefaultStackHint;
            stackHint.EnableInClassList("hint-active", counterMode);

            stackScroll.Clear();
            if (!any)
            {
                stackScroll.Add(Lbl("The stack is empty. Press Cast Spell.", "empty"));
                return;
            }

            for (int i = engine.stack.Count - 1; i >= 0; i--)
            {
                var item = engine.stack[i];
                bool top = i == engine.stack.Count - 1;
                string kindCls = item.kind.ToString().ToLowerInvariant();

                var card = El("card " + kindCls + (top ? " top" : ""));
                if (item.kind == StackKind.Trigger && item.def != null && item.def.id == TriggerDef.KrarkId) AddKrarkArt(card);
                var badges = El("row");
                if (top) badges.Add(Lbl("TOP", "badge top"));
                badges.Add(Lbl(item.kind == StackKind.Trigger ? "Trigger" : item.kind.ToString(), "badge " + kindCls));

                card.Add(El("card-head", Lbl(item.title, "card-title"), badges));
                if (item.kind == StackKind.Trigger) card.Add(Lbl("On: " + item.source + "  |  Spell: " + item.spellTitle, "card-source"));
                foreach (var line in item.lines) card.Add(Lbl(line, "card-line"));

                if (counterMode)
                {
                    var target = item;
                    card.AddToClassList("targetable");
                    card.RegisterCallback<ClickEvent>(_ => engine.CounterTarget(target));
                }
                stackScroll.Add(card);
            }
        }

        /// <summary>Puts Krark's artwork behind a card: right aligned, semi transparent, fading into the card colour.</summary>
        static void AddKrarkArt(VisualElement card)
        {
            // Both layers come first so they sit behind the card content, and never take clicks.
            foreach (var cls in new[] { "art", "art-fade" })
            {
                var layer = El(cls);
                layer.pickingMode = PickingMode.Ignore;
                card.Add(layer);
            }
        }

        void RefreshLog()
        {
            logScroll.Clear();
            if (engine.log.Count == 0) logScroll.Add(Lbl("Nothing resolved yet.", "empty"));
            foreach (var e in engine.log)
                logScroll.Add(Lbl(e.text, "log-line log-" + e.kind.ToString().ToLowerInvariant()));
        }

        // ------------------------------------------------------------------- Board

        VisualElement BuildBoardView()
        {
            var view = El("view");

            var listPanel = El("panel col-main");
            listPanel.Add(Lbl("Triggers on the board", "panel-title"));
            listPanel.Add(BuildThumbBanner());
            boardScroll = new ScrollView(ScrollViewMode.Vertical);
            boardScroll.AddToClassList("fill");
            listPanel.Add(boardScroll);
            view.Add(listPanel);

            var form = El("panel col-side form-panel");
            form.Add(Lbl("New trigger", "panel-title"));

            nameField = new TextField();
            nameField.AddToClassList("grow");
            MakeFloating(nameField, "Trigger name", () => nameField.value, s => nameField.value = s, TouchScreenKeyboardType.Default);
            form.Add(El("form-row", Lbl("Name", "form-label"), nameField));

            condField = new DropdownField(Labels.All<TriggerCondition>(Labels.Condition), 0);
            condField.AddToClassList("grow");
            form.Add(El("form-row", Lbl("Condition", "form-label"), condField));

            addEffectBtn = Btn("+ effect", "btn-small", () => { draft.Add(new EffectDef(EffectType.CopySpell)); RebuildDraft(); });
            form.Add(El("panel-head", Lbl("Effects", "form-label"), addEffectBtn));

            replacementHint = Lbl("Replacement effect: applies as a spell is being copied. It never uses the stack, " +
                                  "so it can't trigger itself. Condition is fixed to Copy Spell.", "stats");
            form.Add(replacementHint);

            draftScroll = new ScrollView(ScrollViewMode.Vertical);
            draftScroll.AddToClassList("draft-scroll");
            form.Add(draftScroll);

            formHint = Lbl("", "hint");
            form.Add(formHint);
            form.Add(Btn("Add to board", "btn-ok", SubmitDraft));
            view.Add(form);

            RebuildDraft();
            return view;
        }

        /// <summary>Banner with the Krark's Thumb art and a custom on/off switch.</summary>
        VisualElement BuildThumbBanner()
        {
            thumbSwitch = El("switch-track", El("switch-knob"));
            thumbBanner = El("thumb-banner",
                El("thumb-icon"),
                El("thumb-text",
                    Lbl("Krark's Thumb", "thumb-name"),
                    Lbl("Each Krark flips two coins. You choose which result to keep.", "thumb-desc")),
                thumbSwitch);

            // The whole banner is the touch target, not just the small switch.
            thumbBanner.RegisterCallback<ClickEvent>(_ => engine.SetKrarksThumb(!engine.krarksThumb));
            return thumbBanner;
        }

        void UpdateThumbSwitch()
        {
            bool on = engine.krarksThumb;
            thumbBanner.EnableInClassList("on", on);
            thumbSwitch.EnableInClassList("on", on);
            thumbBadge.EnableInClassList("hidden", !on);
        }

        void RefreshBoard()
        {
            boardScroll.Clear();
            foreach (var def in engine.board)
            {
                var d = def;
                var card = El("card trigger");
                if (d.id == TriggerDef.KrarkId) AddKrarkArt(card);

                var qtyBox = El("row qty-box",
                    Btn("-", "btn-round", () => engine.ChangeQuantity(d, -1)),
                    Lbl(d.quantity.ToString(), "qty"),
                    Btn("+", "btn-round", () => engine.ChangeQuantity(d, 1)));
                if (!d.builtIn) qtyBox.Add(Btn("×", "btn-delete", () => engine.RemoveTrigger(d)));

                card.Add(El("card-head", Lbl(d.name, "card-title"), qtyBox));
                card.Add(Lbl("When: " + Labels.Condition(d.condition) + (d.IsReplacement ? "  -  replacement effect (no stack)" : ""), "cond"));
                foreach (var line in d.DescribeEffects()) card.Add(Lbl(line, "card-line"));
                boardScroll.Add(card);
            }
        }

        void RebuildDraft()
        {
            draftScroll.Clear();
            for (int i = 0; i < draft.Count; i++)
            {
                var e = draft[i];
                var row = El("effect-row");

                var typeField = new DropdownField(Labels.All<EffectType>(Labels.Effect), (int)e.type);
                typeField.style.minWidth = 190;
                typeField.RegisterValueChangedCallback(_ =>
                {
                    e.type = (EffectType)typeField.index;
                    if (e.type == EffectType.CopySpellAdditional)
                    {
                        // A replacement effect stands alone and is always tied to the Copy Spell condition.
                        e.amount = 1;
                        draft.Clear();
                        draft.Add(e);
                        condField.index = (int)TriggerCondition.CopySpell;
                    }
                    RebuildDraft();
                });
                row.Add(typeField);

                if (e.type != EffectType.ReturnSpellToHand && e.type != EffectType.CopySpellAdditional)
                {
                    row.Add(Lbl(e.type == EffectType.DealDamage ? "Damage" : "Qty", "small-label"));
                    var amount = new IntegerField { value = e.amount };
                    amount.style.width = 64;
                    amount.RegisterValueChangedCallback(ev => e.amount = Mathf.Max(e.type == EffectType.DealDamage ? 0 : 1, ev.newValue));
                    MakeFloating(amount, e.type == EffectType.DealDamage ? "Damage" : "Quantity", () => amount.value.ToString(),
                        s => { if (int.TryParse(s, out int v)) amount.value = v; }, TouchScreenKeyboardType.NumberPad);
                    row.Add(amount);
                }

                if (e.type == EffectType.DealDamage)
                {
                    var target = new DropdownField(Labels.All<DamageTarget>(Labels.Target), (int)e.target);
                    target.style.minWidth = 140;
                    target.RegisterValueChangedCallback(_ => e.target = (DamageTarget)target.index);
                    row.Add(target);
                }

                if (e.type == EffectType.AddCounter)
                {
                    var cname = new TextField { value = e.counterName };
                    cname.style.minWidth = 120;
                    cname.RegisterValueChangedCallback(ev => e.counterName = string.IsNullOrWhiteSpace(ev.newValue) ? "counter" : ev.newValue);
                    MakeFloating(cname, "Counter name", () => cname.value, s => cname.value = s, TouchScreenKeyboardType.Default);
                    row.Add(cname);
                }

                var idx = i;
                row.Add(Btn("×", "btn-delete btn-delete-small", () => { draft.RemoveAt(idx); RebuildDraft(); }));
                draftScroll.Add(row);
            }

            bool replacement = draft.Exists(x => x.type == EffectType.CopySpellAdditional);
            condField.SetEnabled(!replacement);
            addEffectBtn.SetEnabled(!replacement);
            replacementHint.EnableInClassList("hidden", !replacement);
        }

        void SubmitDraft()
        {
            if (draft.Count == 0)
            {
                formHint.text = "Add at least one effect.";
                return;
            }
            formHint.text = "";

            var effects = new List<EffectDef>();
            foreach (var e in draft)
                effects.Add(new EffectDef(e.type, e.amount) { target = e.target, counterName = e.counterName });

            engine.AddTrigger(nameField.value, (TriggerCondition)condField.index, effects);

            nameField.value = "";
            draft.Clear();
            draft.Add(new EffectDef(EffectType.CopySpell));
            RebuildDraft();
        }
    }
}
