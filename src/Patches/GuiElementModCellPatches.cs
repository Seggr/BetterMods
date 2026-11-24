using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BetterMods.Gui;
using BetterMods.Updates;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using SurfaceDrawImage = Vintagestory.API.Common.SurfaceDrawImage;

// The mod card, as one change.
//
// Vanilla sizes a mod row's icon from the ROW height ((int)(Bounds.InnerHeight -
// Bounds.absPaddingY * 2 - 10)), stretches the source bitmap into that square, wraps the title and
// the whole of the detail text (authors, description) at the full row width, and grows the row to
// fit - so rows differ wildly in height and anything BetterMods draws into one collides with text.
// BetterMods replaces that with a fixed-height card (ModCellLayout): icon, a one-line title, the
// version slot level with the on/off box, and a button row (ModCard). The detail moves to a hover
// tooltip (GuiElementModCardTooltip).
//
// GuiElementModCell exposes nothing that controls any of this, and Compose() is private, so a
// subclass cannot override it either. Rather than fork the ~60 line draw method (a prefix that
// returns false) or the whole cell class, each transpiler edit here rewrites single instructions:
//
//   - the conv.i4 that ends the icon size expression becomes a call to ModIconGate.IconSize, which
//     returns the fixed, GUI-scaled box (Compose and UpdateCellHeight);
//   - the call SurfaceDrawImage.Image(ImageSurface, BitmapRef, int, int, int, int) becomes a
//     call to ModIconGate.DrawIcon, same signature, which letterboxes instead of stretching. Matched
//     by the FULL parameter list, not by name: 1.22 has only that BitmapRef overload, but 1.23 adds
//     a BitmapExternal one next to it, and a call whose operand changes signature is invalid IL;
//   - each of Compose's three text draws - this.textUtil.AutobreakAndDrawMultilineTextAt(...) for
//     the title, the detail text and the version - loses its "ldfld textUtil" (now a nop, so the
//     receiver left on the stack is the cell itself) and has its callvirt turned into a static call
//     to ModIconGate.DrawTitle / DrawDetail / DrawRightTopText, which take the cell as an extra
//     first argument and otherwise the very same arguments;
//   - the two 73.0 minimum-row-height literals of UpdateCellHeight become calls to
//     ModIconGate.MinRowHeight, and a postfix pins the row to the card height.
//
// Every swap is stack- and type-neutral. If an anchor pattern is not found on the running game
// build the IL is handed back untouched - and because the edits span two independently patched
// methods, the ones that DID land are switched off as well: they all route through ModIconGate,
// which answers vanilla until the whole set is in place.
//
// Patching the cell element rather than either GUI means both surfaces - the main-menu mods screen
// and the in-game profile dialog - are covered by the same change.

namespace BetterMods.Patches
{
    /// <summary>
    /// The all-or-nothing switch in front of the card edits, and the only thing the rewritten
    /// <c>GuiElementModCell</c> IL actually calls.
    /// <para>
    /// The card is not one edit but several, spread over two methods that Harmony patches
    /// independently: the size expression in <c>Compose</c> (which also sets the text indent), the
    /// icon draw and the three text draws in <c>Compose</c>, the same size expression in
    /// <c>UpdateCellHeight</c>, its minimum row height, and the postfix that pins the card height.
    /// They only describe a coherent row when all of them land. Any proper subset is worse than none:
    /// a one-line title in a row that still grows for a description it no longer draws, buttons
    /// drawn into a row nobody sized for them, a <c>scaled(88)</c> icon inside a 73px row.
    /// </para>
    /// <para>
    /// A transpiler cannot un-edit what the other one did, and the two do not know about each other,
    /// so the decision is made at runtime instead: every rewritten instruction calls in here, and
    /// until both transpilers have reported a clean sweep these methods do exactly what the
    /// instruction they replaced would have. <see cref="ModCellLayout.CardLayoutAvailable"/> reads
    /// the same switch, so the picker trigger, the update marker, the card buttons and the tooltip
    /// all disappear together and both surfaces fall back to vanilla rows.
    /// </para>
    /// <para>
    /// Public because it is called from patched IL. Nothing here may throw: a Cairo draw that dies
    /// mid-<c>Compose</c> leaves the cell blank forever.
    /// </para>
    /// </summary>
    public static class ModIconGate
    {
        private static bool composeLanded;
        private static bool cellHeightLanded;

        /// <summary>True once every card edit has landed. False means "behave like vanilla".</summary>
        public static bool Enabled { get; private set; }

        /// <summary>Every one of <c>Compose</c>'s edits landed.</summary>
        internal static void ReportCompose(bool landed)
        {
            composeLanded = landed;
            Enabled = composeLanded && cellHeightLanded;
        }

        /// <summary>Both of <c>UpdateCellHeight</c>'s edits landed (its postfix rides with them).</summary>
        internal static void ReportUpdateCellHeight(bool landed)
        {
            cellHeightLanded = landed;
            Enabled = composeLanded && cellHeightLanded;
        }

        /// <summary>
        /// Replaces the <c>conv.i4</c> that ends vanilla's icon size expression.
        /// <paramref name="vanillaSize"/> is the double vanilla computed, so the disabled answer is
        /// the cast that was there before - the very same instruction, just spelled in C#.
        /// </summary>
        public static int IconSize(double vanillaSize)
        {
            if (!Enabled) return unchecked((int)vanillaSize);

            return ModCellLayout.LockedIconSize(vanillaSize);
        }

        /// <summary>
        /// Replaces the two <c>73.0</c> literals of <c>UpdateCellHeight</c>'s minimum row height.
        /// Disabled, it hands back that literal.
        /// </summary>
        public static double MinRowHeight()
        {
            if (!Enabled) return ModCellLayout.VanillaMinRowHeight;

            return ModCellLayout.MinUnscaledRowHeight();
        }

        /// <summary>
        /// Replaces the <c>SurfaceDrawImage.Image(ImageSurface, BitmapRef, int, int, int, int)</c>
        /// call (the only overload on 1.22; <c>Compose</c> passes <c>Mod.Icon</c>, a
        /// <c>BitmapExternal</c>, through it). Disabled, it IS that call - the argument evaluation vanilla emitted is untouched, so
        /// forwarding it reproduces vanilla exactly.
        /// <para>
        /// The parameter list has to stay identical to that overload's:
        /// <see cref="ModIconIl.RedirectIconDraw"/> compares the two and refuses to swap the operand
        /// unless they agree.
        /// </para>
        /// </summary>
        public static void DrawIcon(ImageSurface surface, BitmapRef bmp, int xPos, int yPos, int width, int height)
        {
            if (Enabled)
            {
                ModCellLayout.DrawIconFitted(surface, bmp, xPos, yPos, width, height);
                return;
            }

            try
            {
                // Called statically, not as an extension, for the reason ModCellLayout gives.
                SurfaceDrawImage.Image(surface, bmp, xPos, yPos, width, height);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw a mod icon (vanilla passthrough): " + e.Message);
            }
        }

        /// <summary>
        /// Replaces <c>Compose</c>'s title draw. Enabled: one elided line on the card's top line.
        /// Disabled: the vanilla call on the cell's own <c>textUtil</c>, exactly as the IL made it.
        /// The return value is what vanilla stores into <c>titleTextheight</c>.
        /// <para>
        /// The signature is the instance method's with the receiver widened to the cell -
        /// <see cref="ModCardIl.RedirectTextDraws"/> checks that before rewriting anything.
        /// </para>
        /// </summary>
        public static double DrawTitle(GuiElementModCell element, Context ctx, CairoFont font, string text, double posX, double posY, double boxWidth, EnumTextOrientation orientation)
        {
            if (Enabled) return ModCellLayout.DrawCardTitle(element, ctx, font, text);

            return Vanilla(element, ctx, font, text, posX, posY, boxWidth, orientation);
        }

        /// <summary>
        /// Replaces <c>Compose</c>'s detail-text draw - the one text draw that runs for every row,
        /// error rows included - and draws the rest of the card in its place. Disabled: vanilla.
        /// </summary>
        public static double DrawDetail(GuiElementModCell element, Context ctx, CairoFont font, string text, double posX, double posY, double boxWidth, EnumTextOrientation orientation)
        {
            if (Enabled)
            {
                ModCellLayout.DrawCardBody(element, ctx);
                return 0.0;
            }

            return Vanilla(element, ctx, font, text, posX, posY, boxWidth, orientation);
        }

        /// <summary>
        /// Replaces <c>Compose</c>'s <c>RightTopText</c> (version) draw. Enabled it draws nothing: the
        /// card body already drew the version, centred on the on/off box. Disabled: vanilla.
        /// </summary>
        public static double DrawRightTopText(GuiElementModCell element, Context ctx, CairoFont font, string text, double posX, double posY, double boxWidth, EnumTextOrientation orientation)
        {
            if (Enabled) return 0.0;

            return Vanilla(element, ctx, font, text, posX, posY, boxWidth, orientation);
        }

        private static double Vanilla(GuiElementModCell element, Context ctx, CairoFont font, string text, double posX, double posY, double boxWidth, EnumTextOrientation orientation)
        {
            try
            {
                return element.textUtil.AutobreakAndDrawMultilineTextAt(ctx, font, text, posX, posY, boxWidth, orientation);
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not draw a mod row's text (vanilla passthrough): " + e.Message);
                return 0.0;
            }
        }
    }

    internal static class ModIconIl
    {
        /// <summary>Both <c>73.0</c> literals of the minimum-row-height clamp, and nothing else.</summary>
        private const int ExpectedMinRowHeightLiterals = 2;

        /// <summary>
        /// Rewrites the <c>conv.i4</c> that closes <c>... - Bounds.absPaddingY * 2 - 10</c> into a
        /// call to <see cref="ModIconGate.IconSize"/>. Returns false if the pattern is absent.
        /// </summary>
        public static bool LockIconSize(List<CodeInstruction> code)
        {
            MethodInfo lockedSize = AccessTools.Method(typeof(ModIconGate), nameof(ModIconGate.IconSize));
            if (lockedSize == null) return false;

            for (int i = 0; i + 2 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldc_R8) continue;
                if (!(code[i].operand is double constant) || constant != 10.0) continue;
                if (code[i + 1].opcode != OpCodes.Sub) continue;
                if (code[i + 2].opcode != OpCodes.Conv_I4) continue;

                // Mutated in place so any branch label or exception block on the instruction survives.
                code[i + 2].opcode = OpCodes.Call;
                code[i + 2].operand = lockedSize;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Repoints the icon draw at <see cref="ModIconGate.DrawIcon"/>. Only the call operand
        /// changes, so the argument evaluation vanilla already emitted is reused as is.
        /// <para>
        /// Which is only valid while the two methods take exactly the same arguments, so the call is
        /// matched on its whole shape - static, <c>void</c>, and a parameter list type-for-type equal
        /// to <see cref="ModIconGate.DrawIcon"/>'s - not on its name. <c>SurfaceDrawImage.Image</c>
        /// has one overload on 1.22 (<c>BitmapRef</c>, which is what <c>Compose</c> calls) but two on
        /// 1.23 (plus <c>BitmapExternal</c>); a name match would happily swap one for a replacement
        /// shaped like the other. Exactly one such call is expected - more than one and this is not the
        /// method it was written against, so nothing is touched and the row stays vanilla.
        /// </para>
        /// </summary>
        public static bool RedirectIconDraw(List<CodeInstruction> code)
        {
            MethodInfo fittedDraw = AccessTools.Method(typeof(ModIconGate), nameof(ModIconGate.DrawIcon));
            if (fittedDraw == null) return false;

            int found = -1;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call) continue;

                // Matched by declaring type, name and signature rather than by MethodInfo identity:
                // the operand Harmony hands back comes from its own reader and need not be the same
                // instance AccessTools would resolve.
                if (!(code[i].operand is MethodInfo called)) continue;
                if (called.DeclaringType != typeof(SurfaceDrawImage)) continue;
                if (called.Name != nameof(SurfaceDrawImage.Image)) continue;
                if (!SameShape(called, fittedDraw)) continue;

                if (found >= 0) return false;
                found = i;
            }

            if (found < 0) return false;

            code[found].operand = fittedDraw;
            return true;
        }

        /// <summary>
        /// True when a <c>call</c> to <paramref name="original"/> can have its operand replaced by
        /// <paramref name="replacement"/> without touching the stack: both static, same return type,
        /// same parameter types in the same order.
        /// </summary>
        private static bool SameShape(MethodInfo original, MethodInfo replacement)
        {
            if (!original.IsStatic || !replacement.IsStatic) return false;
            if (original.ReturnType != replacement.ReturnType) return false;

            ParameterInfo[] a = original.GetParameters();
            ParameterInfo[] b = replacement.GetParameters();
            if (a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].ParameterType != b[i].ParameterType) return false;
            }

            return true;
        }

        /// <summary>
        /// Rewrites both <c>73.0</c> literals of
        /// <c>if (showModifyIcons &amp;&amp; Bounds.fixedHeight &lt; 73.0) Bounds.fixedHeight = 73.0;</c>
        /// into calls to <see cref="ModIconGate.MinRowHeight"/>, so the row is always tall enough for
        /// the enlarged icon box. A parameterless double-returning call is stack- and type-neutral
        /// against the constant it replaces.
        /// <para>
        /// Counted before anything is rewritten, and only rewritten when the count is the two this
        /// was written against. A method with some other number of <c>73.0</c>s is not the method
        /// this was verified against, and one of them could easily be the comparison without
        /// its assignment - a clamp that reads "if the row is shorter than the new minimum, make it
        /// 73".
        /// </para>
        /// </summary>
        public static bool RaiseMinRowHeight(List<CodeInstruction> code)
        {
            MethodInfo minHeight = AccessTools.Method(typeof(ModIconGate), nameof(ModIconGate.MinRowHeight));
            if (minHeight == null) return false;

            int matches = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (IsMinRowHeightLiteral(code[i])) matches++;
            }

            if (matches != ExpectedMinRowHeightLiterals) return false;

            for (int i = 0; i < code.Count; i++)
            {
                if (!IsMinRowHeightLiteral(code[i])) continue;

                // Mutated in place so any branch label or exception block on the instruction survives.
                code[i].opcode = OpCodes.Call;
                code[i].operand = minHeight;
            }

            return true;
        }

        private static bool IsMinRowHeightLiteral(CodeInstruction instruction)
        {
            if (instruction.opcode != OpCodes.Ldc_R8) return false;

            return instruction.operand is double constant && constant == ModCellLayout.VanillaMinRowHeight;
        }

        public static void ReportMiss(string methodName, string what)
        {
            BetterModsModSystem.Logger?.Warning(
                "[BetterMods] GuiElementModCell." + methodName + " does not look like it did in 1.22.7 (" + what
                + " not found); the mod rows stay vanilla.");
        }
    }

    /// <summary>
    /// Redirects <c>GuiElementModCell.Compose</c>'s three text draws into <see cref="ModIconGate"/>.
    /// <para>
    /// Each is <c>this.textUtil.AutobreakAndDrawMultilineTextAt(ctx, font, text, x, y, width,
    /// orientation)</c>, i.e. <c>ldarg.0; ldfld textUtil; ...args...; callvirt</c> (verified in the
    /// live 1.22.7 IL, same offsets as 1.23.0: <c>IL_017b/017c ... IL_01bb</c> title,
    /// <c>IL_01d6/01d7 ... IL_0229</c> detail, <c>IL_0257/0258 ... IL_02ce</c> version). Turning the <c>ldfld</c> into a <c>nop</c> leaves
    /// the cell itself where the receiver was, and turning the <c>callvirt</c> into a <c>call</c> to a
    /// static that takes the cell first and the same arguments after is then stack- and type-neutral.
    /// The return value (stored as <c>titleTextheight</c>, or popped) is preserved.
    /// </para>
    /// <para>
    /// All three or none: they are told apart by order and checked by their orientation argument -
    /// only the version draw passes <c>EnumTextOrientation.Right</c> (1); the title and the detail text
    /// pass the <c>Left</c> default (0). Any other count or shape and nothing is touched.
    /// </para>
    /// </summary>
    internal static class ModCardIl
    {
        private const int ExpectedTextDraws = 3;

        public static bool RedirectTextDraws(List<CodeInstruction> code)
        {
            FieldInfo textUtil = AccessTools.Field(typeof(GuiElementTextBase), nameof(GuiElementTextBase.textUtil));
            MethodInfo original = AccessTools.Method(typeof(TextDrawUtil), nameof(TextDrawUtil.AutobreakAndDrawMultilineTextAt));
            MethodInfo[] replacements =
            {
                AccessTools.Method(typeof(ModIconGate), nameof(ModIconGate.DrawTitle)),
                AccessTools.Method(typeof(ModIconGate), nameof(ModIconGate.DrawDetail)),
                AccessTools.Method(typeof(ModIconGate), nameof(ModIconGate.DrawRightTopText))
            };
            OpCode[] orientations = { OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1 };

            if (textUtil == null || original == null) return false;
            foreach (MethodInfo replacement in replacements)
            {
                if (replacement == null || !WidensReceiver(original, replacement)) return false;
            }

            List<int> calls = new List<int>();
            for (int i = 0; i < code.Count; i++)
            {
                if (IsCallTo(code[i], typeof(TextDrawUtil), nameof(TextDrawUtil.AutobreakAndDrawMultilineTextAt))) calls.Add(i);
            }

            if (calls.Count != ExpectedTextDraws) return false;

            // Validate all three before touching any.
            int[] receivers = new int[ExpectedTextDraws];
            for (int n = 0; n < ExpectedTextDraws; n++)
            {
                int call = calls[n];
                if (code[call].opcode != OpCodes.Callvirt || call == 0 || code[call - 1].opcode != orientations[n]) return false;

                int receiver = FindReceiverLoad(code, call, textUtil);
                if (receiver < 0 || (n > 0 && receiver <= calls[n - 1])) return false;
                receivers[n] = receiver;
            }

            for (int n = 0; n < ExpectedTextDraws; n++)
            {
                // Mutated in place so any branch label or exception block on either survives.
                CodeInstruction field = code[receivers[n] + 1];
                field.opcode = OpCodes.Nop;
                field.operand = null;

                code[calls[n]].opcode = OpCodes.Call;
                code[calls[n]].operand = replacements[n];
            }

            return true;
        }

        /// <summary>
        /// True when <paramref name="replacement"/> is static, returns what <paramref name="original"/>
        /// returns, and takes the cell followed by exactly <paramref name="original"/>'s parameters.
        /// </summary>
        private static bool WidensReceiver(MethodInfo original, MethodInfo replacement)
        {
            if (original.IsStatic || !replacement.IsStatic) return false;
            if (original.ReturnType != replacement.ReturnType) return false;

            ParameterInfo[] a = original.GetParameters();
            ParameterInfo[] b = replacement.GetParameters();
            if (b.Length != a.Length + 1 || b[0].ParameterType != typeof(GuiElementModCell)) return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].ParameterType != b[i + 1].ParameterType) return false;
            }

            return true;
        }

        /// <summary>
        /// Index of the <c>ldarg.0</c> that opens <c>this.textUtil....</c> ahead of
        /// <paramref name="callIndex"/>, or -1 if the call does not start the way it is expected to.
        /// </summary>
        private static int FindReceiverLoad(List<CodeInstruction> code, int callIndex, FieldInfo textUtil)
        {
            for (int i = callIndex - 1; i > 0; i--)
            {
                if (code[i].opcode != OpCodes.Ldfld) continue;
                if (!(code[i].operand is FieldInfo field) || field.Name != textUtil.Name || field.DeclaringType != textUtil.DeclaringType) continue;

                return code[i - 1].opcode == OpCodes.Ldarg_0 ? i - 1 : -1;
            }

            return -1;
        }

        /// <summary>
        /// Matched by declaring type and name rather than by <c>MethodInfo</c> identity: the operand
        /// Harmony hands back comes from its own reader and need not be the same instance.
        /// </summary>
        private static bool IsCallTo(CodeInstruction instruction, Type declaringType, string name)
        {
            if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) return false;
            if (!(instruction.operand is MethodInfo called)) return false;

            return called.DeclaringType == declaringType && called.Name == name;
        }
    }

    /// <summary>
    /// Folds vanilla's "made for another game version" hover text into the card tooltip.
    /// <para>
    /// The cell's constructor bakes <c>Lang.Get("mod-versionmismatch", ...)</c> into a private
    /// <c>warningTextTexture</c> for a mod that depends on <c>game</c>/<c>creative</c>/<c>survival</c>
    /// at an incompatible major.minor, and <c>OnRenderInteractiveElements</c> draws that texture at
    /// the mouse whenever the cell is hovered - exactly where the card tooltip appears, so the two
    /// would overprint. On a new minor version that is most mods made for the previous one. With the card on, the
    /// texture is disposed and nulled (vanilla null-checks it on both the render and the dispose
    /// path), and the same sentence goes into the tooltip instead. The warning ICON on the card is
    /// untouched. Without the card - or without the field on some build - vanilla is left alone.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(GuiElementModCell), MethodType.Constructor, new[] { typeof(ICoreClientAPI), typeof(ModCellEntry), typeof(ElementBounds), typeof(IAsset) })]
    public static class GuiElementModCell_Ctor_Patch
    {
        private static readonly AccessTools.FieldRef<GuiElementModCell, LoadedTexture> WarningTextTexture =
            GameFields.Ref<GuiElementModCell, LoadedTexture>("warningTextTexture", "the version-mismatch hover text stays vanilla");

        private static readonly string[] BaseGameModIds = { "game", "creative", "survival" };

        [HarmonyPrepare]
        public static bool Prepare()
        {
            if (WarningTextTexture == null) return false;

            ConstructorInfo ctor = AccessTools.Constructor(typeof(GuiElementModCell), new[] { typeof(ICoreClientAPI), typeof(ModCellEntry), typeof(ElementBounds), typeof(IAsset) });
            if (ctor != null) return true;

            BetterModsModSystem.Logger?.Error("[BetterMods] GuiElementModCell's constructor not found on this game version; that patch is skipped.");
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(GuiElementModCell __instance)
        {
            if (!ModIconGate.Enabled || __instance == null) return;

            try
            {
                LoadedTexture texture = WarningTextTexture(__instance);
                if (texture == null) return;

                WarningTextTexture(__instance) = null;
                texture.Dispose();

                // The same test and sentence the constructor used (GuiElementModCell.cs:61-76, 1.22.7 and 1.23).
                ModContainer mod = __instance.cell?.Mod;
                if (mod?.Info?.Dependencies == null || !mod.Enabled) return;

                foreach (ModDependency dependency in mod.Info.Dependencies)
                {
                    if (dependency == null || string.IsNullOrEmpty(dependency.Version) || dependency.Version == "*") continue;
                    if (Array.IndexOf(BaseGameModIds, dependency.ModID) < 0) continue;
                    if (GameVersion.IsCompatibleApiVersion(dependency.Version)) continue;

                    ModCardContent.AppendTooltipWarning(__instance.cell, Lang.Get("mod-versionmismatch", dependency.Version, ModUpdateService.GameVersionChecked));
                }
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not move a mod's version warning into its tooltip: " + e.Message);
            }
        }
    }

    /// <summary>Locks the icon box, letterboxes the icon and draws the card while the row is composed.</summary>
    [HarmonyPatch(typeof(GuiElementModCell), "Compose")]
    public static class GuiElementModCell_Compose_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(GuiElementModCell), "Compose", Type.EmptyTypes);

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            try
            {
                // Every edit here, and both of UpdateCellHeight's, have to land before any of them
                // takes effect - see ModIconGate. Separate statements rather than one && so a miss
                // on the first still reports the rest.
                bool sized = ModIconIl.LockIconSize(code);
                if (!sized) ModIconIl.ReportMiss("Compose", "the icon size expression");

                bool drawn = ModIconIl.RedirectIconDraw(code);
                if (!drawn) ModIconIl.ReportMiss("Compose", "the icon draw call");

                bool texts = ModCardIl.RedirectTextDraws(code);
                if (!texts) ModIconIl.ReportMiss("Compose", "the three text draws");

                ModIconGate.ReportCompose(sized && drawn && texts);
            }
            catch (Exception e)
            {
                ModIconGate.ReportCompose(false);
                BetterModsModSystem.Logger?.Error("[BetterMods] Rewriting GuiElementModCell.Compose failed; mod rows stay vanilla.");
                BetterModsModSystem.Logger?.Error(e);
            }

            return code;
        }
    }

    /// <summary>
    /// The same size expression decides how much width the text loses to the icon, so the row height
    /// calculation has to agree with <c>Compose</c>; and the card is a fixed height, which the postfix
    /// enforces over whatever vanilla measured for text the card no longer draws.
    /// </summary>
    [HarmonyPatch(typeof(GuiElementModCell), "UpdateCellHeight")]
    public static class GuiElementModCell_UpdateCellHeight_Patch
    {
        [HarmonyPrepare]
        public static bool Prepare() => PatchTarget.Exists(typeof(GuiElementModCell), "UpdateCellHeight", Type.EmptyTypes);

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            try
            {
                bool sized = ModIconIl.LockIconSize(code);
                if (!sized) ModIconIl.ReportMiss("UpdateCellHeight", "the icon size expression");

                bool raised = ModIconIl.RaiseMinRowHeight(code);
                if (!raised) ModIconIl.ReportMiss("UpdateCellHeight", "the minimum row height");

                ModIconGate.ReportUpdateCellHeight(sized && raised);
            }
            catch (Exception e)
            {
                ModIconGate.ReportUpdateCellHeight(false);
                BetterModsModSystem.Logger?.Error("[BetterMods] Rewriting GuiElementModCell.UpdateCellHeight failed; row heights stay vanilla.");
                BetterModsModSystem.Logger?.Error(e);
            }

            return code;
        }

        /// <summary>
        /// Pins the row to the card height. Vanilla has just measured the wrapped title and the whole
        /// detail text - neither of which the card draws - and the transpiled minimum only ever
        /// RAISES a row, so without this a mod with a long description would still get a tall, mostly
        /// empty card. Gated like every other edit: a no-op while the card is off. In the same patch
        /// class as the transpiler, so the two are applied (or skipped) together.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(GuiElementModCell __instance)
        {
            if (!ModIconGate.Enabled) return;

            try
            {
                if (__instance?.Bounds != null) __instance.Bounds.fixedHeight = ModCellLayout.MinUnscaledRowHeight();
            }
            catch (Exception e)
            {
                BetterModsModSystem.Logger?.Warning("[BetterMods] Could not pin a mod card's height: " + e.Message);
            }
        }
    }
}
