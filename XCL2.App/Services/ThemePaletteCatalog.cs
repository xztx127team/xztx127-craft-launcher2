using System.Collections.Generic;

namespace XCL2.App.Services;

/// <summary>完整主题资源；保持为纯色值，便于在不加载 WPF 的回归项目中验证。</summary>
internal sealed record Palette(
    string Accent, string AccentHover, string Glow, string GlowSoft,
    string Panel, string Side, string Border, string BorderHover,
    string TextPrimary, string TextSecondary,
    string TileBlue, string TileIndigo, string TileGreen, string TileOrange, string TilePurple,
    string SuccessText, string WarningText, string WarningBanner, string Danger, string Divider,
    string ButtonBackground, string ButtonHoverBackground, string ButtonForeground);

/// <summary>
/// 新增色系各自提供浅色和深色两套完整资源。背景、文字、按钮、语义提示和卡片都保持可读，
/// 而不是只替换强调色；ThemeService 负责把这些色值应用到所有全局画刷。
/// </summary>
internal static class ThemePaletteCatalog
{
    internal const string SkinSakura = "Sakura";
    internal const string SkinForest = "Forest";
    internal const string SkinSeaSalt = "SeaSalt";
    internal const string SkinCoffee = "Coffee";
    internal const string SkinGrape = "Grape";
    internal const string SkinBerry = "Berry";
    internal const string SkinDune = "Dune";
    internal const string SkinMidnight = "Midnight";

    internal const string SkinJade = "Jade";
    internal const string SkinCoral = "Coral";
    internal const string SkinOcean = "Ocean";
    internal const string SkinLavender = "Lavender";
    internal const string SkinSlate = "Slate";
    internal const string SkinSunflower = "Sunflower";

    internal static readonly IReadOnlyDictionary<(string Hue, bool Dark), Palette> Palettes =
        new Dictionary<(string Hue, bool Dark), Palette>
        {
            // 樱花：柔和花瓣粉，深色版保留温暖的玫瑰灰背景。
            [(SkinSakura, false)] = new Palette(
                Accent: "#AC466A", AccentHover: "#90334F", Glow: "#ED9AB6", GlowSoft: "#FBE3EC",
                Panel: "#FFF7F9", Side: "#F8EBEF", Border: "#DDBDCB", BorderHover: "#B26D88",
                TextPrimary: "#35202A", TextSecondary: "#6D4D5B",
                TileBlue: "#E4EDF7", TileIndigo: "#ECE7F6", TileGreen: "#E4F0E7", TileOrange: "#FBEADC", TilePurple: "#F3E3EE",
                SuccessText: "#237342", WarningText: "#935817", WarningBanner: "#FFF0D9", Danger: "#AE3747", Divider: "#E4CCD5",
                ButtonBackground: "#F2CFDD", ButtonHoverBackground: "#E7B6CA", ButtonForeground: "#64283E"),
            [(SkinSakura, true)] = new Palette(
                Accent: "#EF9BB8", AccentHover: "#F5B4CA", Glow: "#F5A6C5", GlowSoft: "#422935",
                Panel: "#2A1C25", Side: "#1A1218", Border: "#60414F", BorderHover: "#866073",
                TextPrimary: "#FAF0F4", TextSecondary: "#D4B8C4",
                TileBlue: "#213246", TileIndigo: "#302943", TileGreen: "#25392C", TileOrange: "#423026", TilePurple: "#3C293D",
                SuccessText: "#94D6AD", WarningText: "#EAC38E", WarningBanner: "#423223", Danger: "#F09BAA", Divider: "#60414F",
                ButtonBackground: "#533443", ButtonHoverBackground: "#6A4155", ButtonForeground: "#FFF0F5"),

            // 森林：苔绿和木叶绿，避免使用刺眼的高饱和绿色。
            [(SkinForest, false)] = new Palette(
                Accent: "#33724C", AccentHover: "#275B3B", Glow: "#71B68A", GlowSoft: "#DDEEE2",
                Panel: "#F4F9F4", Side: "#E7F0E8", Border: "#BCD3C2", BorderHover: "#6E9E7B",
                TextPrimary: "#1C3023", TextSecondary: "#4E6A57",
                TileBlue: "#E1EDF2", TileIndigo: "#E5E8F2", TileGreen: "#DAECDE", TileOrange: "#F2E8D9", TilePurple: "#EBE4EF",
                SuccessText: "#286C3F", WarningText: "#936022", WarningBanner: "#F7EDD6", Danger: "#AB4141", Divider: "#CADCCE",
                ButtonBackground: "#CFE4D5", ButtonHoverBackground: "#B5D5BE", ButtonForeground: "#254A30"),
            [(SkinForest, true)] = new Palette(
                Accent: "#8ACAA1", AccentHover: "#A7DCB7", Glow: "#96D3AA", GlowSoft: "#24402E",
                Panel: "#18281D", Side: "#101A14", Border: "#375641", BorderHover: "#527B60",
                TextPrimary: "#EDF6EF", TextSecondary: "#AFCAB8",
                TileBlue: "#203640", TileIndigo: "#2A3045", TileGreen: "#284532", TileOrange: "#3E3525", TilePurple: "#343046",
                SuccessText: "#94D8AC", WarningText: "#E7C18D", WarningBanner: "#3E3422", Danger: "#F09F9F", Divider: "#375641",
                ButtonBackground: "#2C4E36", ButtonHoverBackground: "#3A6146", ButtonForeground: "#ECF8F0"),

            // 海盐：清淡蓝绿灰，与鲜亮的水主题保持不同观感。
            [(SkinSeaSalt, false)] = new Palette(
                Accent: "#397786", AccentHover: "#2E6371", Glow: "#83C6CC", GlowSoft: "#D9EFF0",
                Panel: "#F3FAFB", Side: "#E4EFF1", Border: "#B9D4D8", BorderHover: "#699CA6",
                TextPrimary: "#1D333A", TextSecondary: "#4A6974",
                TileBlue: "#DDEDF2", TileIndigo: "#E3EAF5", TileGreen: "#DFF0E8", TileOrange: "#F2EADC", TilePurple: "#E9E6F3",
                SuccessText: "#287149", WarningText: "#926022", WarningBanner: "#F8EFD9", Danger: "#AA444B", Divider: "#C9DEE1",
                ButtonBackground: "#CBE2E7", ButtonHoverBackground: "#B0D1D9", ButtonForeground: "#214957"),
            [(SkinSeaSalt, true)] = new Palette(
                Accent: "#8ED2D8", AccentHover: "#AAE2E6", Glow: "#9BDCE0", GlowSoft: "#24434A",
                Panel: "#192B31", Side: "#111E23", Border: "#3A5862", BorderHover: "#577D87",
                TextPrimary: "#EDF8FA", TextSecondary: "#ADCBD1",
                TileBlue: "#23404C", TileIndigo: "#2A344A", TileGreen: "#234237", TileOrange: "#3E372B", TilePurple: "#343447",
                SuccessText: "#91D8AF", WarningText: "#E9C48F", WarningBanner: "#3D3324", Danger: "#EEA0A8", Divider: "#3A5862",
                ButtonBackground: "#2C4F5A", ButtonHoverBackground: "#396474", ButtonForeground: "#EAF9FC"),

            // 咖啡：奶油和咖啡棕，浅色温暖、深色沉稳。
            [(SkinCoffee, false)] = new Palette(
                Accent: "#8B5B3A", AccentHover: "#72482D", Glow: "#CAA785", GlowSoft: "#EFE0CE",
                Panel: "#FBF7F2", Side: "#F0E7DC", Border: "#D5C0AA", BorderHover: "#AA8864",
                TextPrimary: "#33271E", TextSecondary: "#6E5948",
                TileBlue: "#E3ECF0", TileIndigo: "#E7E5EF", TileGreen: "#E3EBDF", TileOrange: "#F5E2CF", TilePurple: "#EDE2EA",
                SuccessText: "#396F43", WarningText: "#91581C", WarningBanner: "#F8EACF", Danger: "#AE4340", Divider: "#DECEBC",
                ButtonBackground: "#E8D4BD", ButtonHoverBackground: "#DCBE9E", ButtonForeground: "#5B3823"),
            [(SkinCoffee, true)] = new Palette(
                Accent: "#DDB68D", AccentHover: "#E8C9A7", Glow: "#DDB68D", GlowSoft: "#493629",
                Panel: "#2D251F", Side: "#1E1814", Border: "#634C3B", BorderHover: "#866C53",
                TextPrimary: "#FAF2E9", TextSecondary: "#D1BBA7",
                TileBlue: "#263745", TileIndigo: "#313044", TileGreen: "#2D3C2B", TileOrange: "#493629", TilePurple: "#3E2D3D",
                SuccessText: "#A2D3A5", WarningText: "#EDC08A", WarningBanner: "#44341F", Danger: "#F0A19A", Divider: "#634C3B",
                ButtonBackground: "#533E2D", ButtonHoverBackground: "#6A503A", ButtonForeground: "#FCF0E1"),

            // 葡萄：低饱和灰紫，适合长时间阅读。
            [(SkinGrape, false)] = new Palette(
                Accent: "#7752A0", AccentHover: "#604183", Glow: "#AB93D0", GlowSoft: "#E7DCF2",
                Panel: "#F8F6FC", Side: "#EEE8F5", Border: "#CDC0E0", BorderHover: "#9E87BB",
                TextPrimary: "#2B203B", TextSecondary: "#665575",
                TileBlue: "#E0EBF5", TileIndigo: "#E6E2F4", TileGreen: "#E3EEE4", TileOrange: "#F4E8DC", TilePurple: "#E8DDF3",
                SuccessText: "#2D7045", WarningText: "#925D23", WarningBanner: "#F8EED8", Danger: "#AC4053", Divider: "#D9CDE8",
                ButtonBackground: "#DFD1ED", ButtonHoverBackground: "#CDBAE1", ButtonForeground: "#48305F"),
            [(SkinGrape, true)] = new Palette(
                Accent: "#BCA1E3", AccentHover: "#CDB7ED", Glow: "#C3ACE8", GlowSoft: "#3A2B52",
                Panel: "#241D32", Side: "#181322", Border: "#4F3F6A", BorderHover: "#725D93",
                TextPrimary: "#F7F1FE", TextSecondary: "#C8B5DE",
                TileBlue: "#25344C", TileIndigo: "#322C50", TileGreen: "#293B31", TileOrange: "#403328", TilePurple: "#3A2B52",
                SuccessText: "#98D6AF", WarningText: "#EAC18C", WarningBanner: "#403222", Danger: "#EE9FAB", Divider: "#4F3F6A",
                ButtonBackground: "#493560", ButtonHoverBackground: "#60477B", ButtonForeground: "#F5EBFE"),

            // 莓果：比樱花更浓郁的暖红紫，保持正文的清晰度。
            [(SkinBerry, false)] = new Palette(
                Accent: "#A7415D", AccentHover: "#8B3049", Glow: "#DD8DA5", GlowSoft: "#F5DCE5",
                Panel: "#FCF5F7", Side: "#F4E5EA", Border: "#DAB8C4", BorderHover: "#B7758B",
                TextPrimary: "#3B202B", TextSecondary: "#76525F",
                TileBlue: "#E3EBF4", TileIndigo: "#E9E2F2", TileGreen: "#E3EEE6", TileOrange: "#F7E5D8", TilePurple: "#EFDFEC",
                SuccessText: "#2B7044", WarningText: "#925B20", WarningBanner: "#F9EBD4", Danger: "#A52F45", Divider: "#E2C9D3",
                ButtonBackground: "#EDCADA", ButtonHoverBackground: "#E0ACC2", ButtonForeground: "#60243B"),
            [(SkinBerry, true)] = new Palette(
                Accent: "#E99BB4", AccentHover: "#F1B3C7", Glow: "#EEA1B9", GlowSoft: "#4A293B",
                Panel: "#301C28", Side: "#20121B", Border: "#673E50", BorderHover: "#8C5870",
                TextPrimary: "#FBEFF5", TextSecondary: "#D9B5C5",
                TileBlue: "#273349", TileIndigo: "#352B47", TileGreen: "#2C3B2E", TileOrange: "#453026", TilePurple: "#432B42",
                SuccessText: "#9BD6AD", WarningText: "#EEC48F", WarningBanner: "#443120", Danger: "#F4A1AC", Divider: "#673E50",
                ButtonBackground: "#593348", ButtonHoverBackground: "#71425C", ButtonForeground: "#FFF0F7"),

            // 沙丘：砂岩与燕麦色，强调色克制，适合暖色界面。
            [(SkinDune, false)] = new Palette(
                Accent: "#8A6A2F", AccentHover: "#6F5525", Glow: "#C8B277", GlowSoft: "#EEE4C9",
                Panel: "#FCFAF2", Side: "#F3ECDD", Border: "#D5C6A0", BorderHover: "#B49A61",
                TextPrimary: "#362C1B", TextSecondary: "#756449",
                TileBlue: "#E3EDF0", TileIndigo: "#E8E8F0", TileGreen: "#E6EDDA", TileOrange: "#F4E4C9", TilePurple: "#EDE5EE",
                SuccessText: "#4B703C", WarningText: "#8E581B", WarningBanner: "#F9E8C8", Danger: "#AC4440", Divider: "#DDCFB3",
                ButtonBackground: "#EADFBD", ButtonHoverBackground: "#DED09E", ButtonForeground: "#5E461C"),
            [(SkinDune, true)] = new Palette(
                Accent: "#D8C18B", AccentHover: "#E7D3A5", Glow: "#DDC997", GlowSoft: "#443A24",
                Panel: "#2B261A", Side: "#1C1912", Border: "#5C5035", BorderHover: "#81724B",
                TextPrimary: "#F8F3E6", TextSecondary: "#D0C4A6",
                TileBlue: "#283846", TileIndigo: "#333345", TileGreen: "#343E29", TileOrange: "#473923", TilePurple: "#3B3141",
                SuccessText: "#B5D398", WarningText: "#ECC68D", WarningBanner: "#463720", Danger: "#EFA29A", Divider: "#5C5035",
                ButtonBackground: "#51442A", ButtonHoverBackground: "#685839", ButtonForeground: "#FFF3D8"),

            // 午夜：蓝灰和靛蓝；浅色版也有完整的日间资源，不强制用户切换深色。
            [(SkinMidnight, false)] = new Palette(
                Accent: "#4B5DA2", AccentHover: "#3A4983", Glow: "#879EDB", GlowSoft: "#DEE5F8",
                Panel: "#F4F6FC", Side: "#E7EBF5", Border: "#BEC9E5", BorderHover: "#8494C3",
                TextPrimary: "#202A47", TextSecondary: "#58698C",
                TileBlue: "#DDE9F5", TileIndigo: "#E0E4F5", TileGreen: "#E1EFE8", TileOrange: "#F5E8DC", TilePurple: "#E7E0F5",
                SuccessText: "#286D4A", WarningText: "#915C27", WarningBanner: "#F8EFD9", Danger: "#AD4156", Divider: "#CCD4E8",
                ButtonBackground: "#CCD5F1", ButtonHoverBackground: "#B0BDE6", ButtonForeground: "#2B3B75"),
            [(SkinMidnight, true)] = new Palette(
                Accent: "#9CB5F5", AccentHover: "#B6C9FA", Glow: "#A4BCF9", GlowSoft: "#223353",
                Panel: "#131C31", Side: "#0C1323", Border: "#34486E", BorderHover: "#4C6491",
                TextPrimary: "#EFF3FD", TextSecondary: "#B1BEDD",
                TileBlue: "#1F3453", TileIndigo: "#262E50", TileGreen: "#21392E", TileOrange: "#3D3028", TilePurple: "#312D4E",
                SuccessText: "#8ACCA7", WarningText: "#E9C18E", WarningBanner: "#3C3020", Danger: "#F0A0AB", Divider: "#34486E",
                ButtonBackground: "#2C406D", ButtonHoverBackground: "#3B548A", ButtonForeground: "#F2F5FF"),

            // 低饱和的补充色系，每套提供独立的日间和夜间资源。
            [(SkinJade, false)] = new Palette(
                Accent: "#267461", AccentHover: "#195A49", Glow: "#AFD6C8", GlowSoft: "#D0E9E0",
                Panel: "#F4FAF7", Side: "#E5F1EA", Border: "#B6D2C5", BorderHover: "#77A591",
                TextPrimary: "#182332", TextSecondary: "#4B5869", TileBlue: "#E2ECF6", TileIndigo: "#E7E3F3",
                TileGreen: "#E2EEE5", TileOrange: "#F4E6D7", TilePurple: "#EEE4F2", SuccessText: "#237342",
                WarningText: "#84521D", WarningBanner: "#FFF2DC", Danger: "#AE3747", Divider: "#B6D2C5",
                ButtonBackground: "#D0E9E0", ButtonHoverBackground: "#AFD6C8", ButtonForeground: "#21483C"),
            [(SkinJade, true)] = new Palette(
                Accent: "#88D1B7", AccentHover: "#ACE1CD", Glow: "#ACE1CD", GlowSoft: "#193F35",
                Panel: "#18302A", Side: "#10221D", Border: "#365B4D", BorderHover: "#578571",
                TextPrimary: "#F3F5FA", TextSecondary: "#BEC9D7", TileBlue: "#26394D", TileIndigo: "#33304B",
                TileGreen: "#2A4032", TileOrange: "#45352A", TilePurple: "#3C3049", SuccessText: "#9BD9B4",
                WarningText: "#F0C994", WarningBanner: "#45351F", Danger: "#F1A5AD", Divider: "#365B4D",
                ButtonBackground: "#193F35", ButtonHoverBackground: "#365B4D", ButtonForeground: "#F5F7FB"),
            [(SkinCoral, false)] = new Palette(
                Accent: "#AE4A3D", AccentHover: "#8D382E", Glow: "#E9BCAE", GlowSoft: "#F3D7CF",
                Panel: "#FFF7F3", Side: "#F7E7DF", Border: "#E1C2B5", BorderHover: "#B67A68",
                TextPrimary: "#182332", TextSecondary: "#4B5869", TileBlue: "#E2ECF6", TileIndigo: "#E7E3F3",
                TileGreen: "#E2EEE5", TileOrange: "#F4E6D7", TilePurple: "#EEE4F2", SuccessText: "#237342",
                WarningText: "#84521D", WarningBanner: "#FFF2DC", Danger: "#AE3747", Divider: "#E1C2B5",
                ButtonBackground: "#F3D7CF", ButtonHoverBackground: "#E9BCAE", ButtonForeground: "#632C24"),
            [(SkinCoral, true)] = new Palette(
                Accent: "#F3AD9B", AccentHover: "#FAC6B8", Glow: "#FAC6B8", GlowSoft: "#522B25",
                Panel: "#30211E", Side: "#211613", Border: "#69463B", BorderHover: "#936251",
                TextPrimary: "#F3F5FA", TextSecondary: "#BEC9D7", TileBlue: "#26394D", TileIndigo: "#33304B",
                TileGreen: "#2A4032", TileOrange: "#45352A", TilePurple: "#3C3049", SuccessText: "#9BD9B4",
                WarningText: "#F0C994", WarningBanner: "#45351F", Danger: "#F1A5AD", Divider: "#69463B",
                ButtonBackground: "#522B25", ButtonHoverBackground: "#69463B", ButtonForeground: "#F5F7FB"),
            [(SkinOcean, false)] = new Palette(
                Accent: "#236B98", AccentHover: "#185579", Glow: "#B3D5E9", GlowSoft: "#D4E8F4",
                Panel: "#F4F9FD", Side: "#E4EEF7", Border: "#B9CCDF", BorderHover: "#7096B5",
                TextPrimary: "#182332", TextSecondary: "#4B5869", TileBlue: "#E2ECF6", TileIndigo: "#E7E3F3",
                TileGreen: "#E2EEE5", TileOrange: "#F4E6D7", TilePurple: "#EEE4F2", SuccessText: "#237342",
                WarningText: "#84521D", WarningBanner: "#FFF2DC", Danger: "#AE3747", Divider: "#B9CCDF",
                ButtonBackground: "#D4E8F4", ButtonHoverBackground: "#B3D5E9", ButtonForeground: "#1C425D"),
            [(SkinOcean, true)] = new Palette(
                Accent: "#92C9EE", AccentHover: "#B6DCF6", Glow: "#B6DCF6", GlowSoft: "#203E58",
                Panel: "#192837", Side: "#101C29", Border: "#37546D", BorderHover: "#547D9C",
                TextPrimary: "#F3F5FA", TextSecondary: "#BEC9D7", TileBlue: "#26394D", TileIndigo: "#33304B",
                TileGreen: "#2A4032", TileOrange: "#45352A", TilePurple: "#3C3049", SuccessText: "#9BD9B4",
                WarningText: "#F0C994", WarningBanner: "#45351F", Danger: "#F1A5AD", Divider: "#37546D",
                ButtonBackground: "#203E58", ButtonHoverBackground: "#37546D", ButtonForeground: "#F5F7FB"),
            [(SkinLavender, false)] = new Palette(
                Accent: "#6C55A0", AccentHover: "#51407D", Glow: "#CCBCE6", GlowSoft: "#E2D9F1",
                Panel: "#F9F7FD", Side: "#EEEAF6", Border: "#CBC0DF", BorderHover: "#9982B8",
                TextPrimary: "#182332", TextSecondary: "#4B5869", TileBlue: "#E2ECF6", TileIndigo: "#E7E3F3",
                TileGreen: "#E2EEE5", TileOrange: "#F4E6D7", TilePurple: "#EEE4F2", SuccessText: "#237342",
                WarningText: "#84521D", WarningBanner: "#FFF2DC", Danger: "#AE3747", Divider: "#CBC0DF",
                ButtonBackground: "#E2D9F1", ButtonHoverBackground: "#CCBCE6", ButtonForeground: "#40305B"),
            [(SkinLavender, true)] = new Palette(
                Accent: "#C1ADEB", AccentHover: "#D7C8F6", Glow: "#D7C8F6", GlowSoft: "#3D3156",
                Panel: "#282133", Side: "#1B1624", Border: "#554467", BorderHover: "#77618D",
                TextPrimary: "#F3F5FA", TextSecondary: "#BEC9D7", TileBlue: "#26394D", TileIndigo: "#33304B",
                TileGreen: "#2A4032", TileOrange: "#45352A", TilePurple: "#3C3049", SuccessText: "#9BD9B4",
                WarningText: "#F0C994", WarningBanner: "#45351F", Danger: "#F1A5AD", Divider: "#554467",
                ButtonBackground: "#3D3156", ButtonHoverBackground: "#554467", ButtonForeground: "#F5F7FB"),
            [(SkinSlate, false)] = new Palette(
                Accent: "#526571", AccentHover: "#3F505C", Glow: "#C3D2DA", GlowSoft: "#DCE5E9",
                Panel: "#F6F8FA", Side: "#E9EEF1", Border: "#C0CDD5", BorderHover: "#8A9FAB",
                TextPrimary: "#182332", TextSecondary: "#4B5869", TileBlue: "#E2ECF6", TileIndigo: "#E7E3F3",
                TileGreen: "#E2EEE5", TileOrange: "#F4E6D7", TilePurple: "#EEE4F2", SuccessText: "#237342",
                WarningText: "#84521D", WarningBanner: "#FFF2DC", Danger: "#AE3747", Divider: "#C0CDD5",
                ButtonBackground: "#DCE5E9", ButtonHoverBackground: "#C3D2DA", ButtonForeground: "#2B414E"),
            [(SkinSlate, true)] = new Palette(
                Accent: "#B7CDD9", AccentHover: "#D1E1E9", Glow: "#D1E1E9", GlowSoft: "#31434E",
                Panel: "#232B31", Side: "#171E23", Border: "#465963", BorderHover: "#68808D",
                TextPrimary: "#F3F5FA", TextSecondary: "#BEC9D7", TileBlue: "#26394D", TileIndigo: "#33304B",
                TileGreen: "#2A4032", TileOrange: "#45352A", TilePurple: "#3C3049", SuccessText: "#9BD9B4",
                WarningText: "#F0C994", WarningBanner: "#45351F", Danger: "#F1A5AD", Divider: "#465963",
                ButtonBackground: "#31434E", ButtonHoverBackground: "#465963", ButtonForeground: "#F5F7FB"),
            [(SkinSunflower, false)] = new Palette(
                Accent: "#886713", AccentHover: "#6C500D", Glow: "#E3D18F", GlowSoft: "#F1E5BA",
                Panel: "#FFFCF1", Side: "#F6EFD9", Border: "#DFD1A2", BorderHover: "#B89C49",
                TextPrimary: "#182332", TextSecondary: "#4B5869", TileBlue: "#E2ECF6", TileIndigo: "#E7E3F3",
                TileGreen: "#E2EEE5", TileOrange: "#F4E6D7", TilePurple: "#EEE4F2", SuccessText: "#237342",
                WarningText: "#84521D", WarningBanner: "#FFF2DC", Danger: "#AE3747", Divider: "#DFD1A2",
                ButtonBackground: "#F1E5BA", ButtonHoverBackground: "#E3D18F", ButtonForeground: "#54400D"),
            [(SkinSunflower, true)] = new Palette(
                Accent: "#E4CB7A", AccentHover: "#F2DE9D", Glow: "#F2DE9D", GlowSoft: "#4E4020",
                Panel: "#302A1B", Side: "#201C12", Border: "#625437", BorderHover: "#8D794B",
                TextPrimary: "#F3F5FA", TextSecondary: "#BEC9D7", TileBlue: "#26394D", TileIndigo: "#33304B",
                TileGreen: "#2A4032", TileOrange: "#45352A", TilePurple: "#3C3049", SuccessText: "#9BD9B4",
                WarningText: "#F0C994", WarningBanner: "#45351F", Danger: "#F1A5AD", Divider: "#625437",
                ButtonBackground: "#4E4020", ButtonHoverBackground: "#625437", ButtonForeground: "#F5F7FB"),
        };
}
