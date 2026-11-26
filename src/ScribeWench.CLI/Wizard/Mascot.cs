using Spectre.Console;
using Spectre.Console.Rendering;

namespace ScribeWench.CLI.Wizard;

// The scribe on setup's first screen, drawn in braille: eight dots to a character. Dots holds the
// characters and Inks the colour of each, by its letter in Palette.
public static class Mascot
{
    // Where the empty corner above her quill begins. The rows that get a caption end before it.
    public const int Corner = 38;

    // With fewer rows than this the hand that holds the quill is cut off.
    public const int Least = 15;

    public static int Height => Dots.Length;

    // One row as runs of a single colour, filled out with spaces to `width`.
    public static IEnumerable<Segment> Row(int row, int width = 0)
    {
        var (dots, inks) = (Dots[row], Inks[row]);
        for (var start = 0; start < dots.Length;)
        {
            var end = start + 1;
            while (end < dots.Length && inks[end] == inks[start]) end++;
            yield return inks[start] == ' '
                ? new Segment(dots[start..end])
                : new Segment(dots[start..end], new Style(Palette[inks[start]]));
            start = end;
        }
        if (dots.Length < width) yield return new Segment(new string(' ', width - dots.Length));
    }

    private static readonly string[] Dots = """
                   ⣀⡠⠤⠒⠒⠒⠒⠒⠢⠤⣀⡀
                ⡠⠔⠉           ⠈⠒⠤⡀
              ⡠⢊⣄⠄   ⡀  ⠂⢉⢂⠒⠄⡀   ⠈⠢⡀
             ⢠⢃⡋⠥⠞⠓⠖⠚⠥⢤⣀⣀⠈ ⣀⠠⢅ ⢒⡀  ⠑⡄
             ⠈⡢⠁        ⠉⠛⠲⢬⣷⣄⡀⠢ ⠄ ⢰⢸
            ⢀⠜              ⠉⠛⠶⣤⣄⡀⣀⡕⡸
         ⢀⣀⡠⠊       ⢠⠄  ⡀ ⠘⡄        ⢇
          ⠉⡱          ⢀⡀⠱⡀ ⠈  ⠰⡄    ⢸
          ⢀⠇  ⡄ ⣠⠤⣆   ⠈⣄⡀⠹     ⠁    ⢸
          ⢸⢢⢀ ⠁⡞  ⠈⡇ ⢠⠊⠁⠉⠳⡀         ⡜                               ⢀⡠⡄
          ⢸⠘⡄⠱⡄⢧ ⠸⢇⡗⠛⢏  ⢰⡆⡇        ⡰⠁                           ⣀⡠⠴⣴⠕⢋⠇
           ⢣⠱⡀⠑⢄⠙⢖⠉  ⠘⠦⣀⣠⠞⠁⢀⠆ ⡀⢀⠤⢊⡖⠁                    ⢀⣀⠤⠤⢔⠒⠉⠉⠳⣠⠞⠙⢃⠎
            ⠁⠑⠢⣑⠣⣈⣣⡒⠤⠄  ⢀⡠⠖⠃  ⠇⡇ ⠊                   ⢀⠔⠊⠁⣄  ⠈⣦⡠⠴⠚⢁⡠⠔⠁
                ⢣⠈ ⠹⡶⢤⣴⡒⠋⠤⠂  ⢶⠆⠣⠤⠤⠤⠒⠒⠢⡀         ⢀⡀  ⢠⣃⣀⣠⣤⣤⣗⠚⠉⣁⣀⠤⠒⠁
                 ⠣⡀⣠⣦⡈⠘⠩⡄     ⢀   ⠈⠓⢦⡀⠑⢄       ⣀⠎⠈⠒⠊⠉⡠⠜    ⠉⠉
               ⣀⠤⠔⠁⠁⠈⠋  ⢁⡀    ⣾⣇   ⠠⡟  ⠈⢢ ⠠⠴⠒⠮⢍      ⡇
              ⡎⢀⣀⣀⡀ ⠴⣠⠖ ⡫⠉⠉⢉⣟⡘⠋⠘⠶⠶ ⠈⠤⠴⠤⠄ ⠣⡀   ⡸    ⢠⠊
           ⢀⡠⠔⠁⠘⣧⢀⠦  ⢡⡤⢞⠁ ⣼⠋⠉⠑⡆⡀ ⠐⢤⡀    ⠟ ⢣  ⢀⠇   ⢠⠃
        ⢠⠔⠊⠁    ⠟⠁⢀⡞⡿⠆⡇⡔⢄⠺⠃   ⢿⣧   ⠈⠓⠦⣀    ⢇ ⡎   ⢀⠎
        """.Split('\n');

    private static readonly string[] Inks = """
                   abccccccccbd
                bef           fcca
              afff   f  gggggg   fch
             ibebbbbbcfffg jjg gg  fa
             dak        kjbffjbb g ff
            lm              kneeejeeb
         lhmk       kk  m mm        h
          om          mhmm m  mm    m
          lm  k omk   mmlm     m    m
          dhk kd  ak mlddoo         h                               ldd
          ddknnm ommmm  ann        md                           liiihhd
           ihkkkkdd  lllddokk kkkmhd                    ldldiiddhhhiph
            lodokmolll  llhk  ko d                   lldii  ioldhpphd
                dl dodddooi  nnkaqhrrrd         ld  lllihiihmmhhii
                 dldrmrrd     m   nnprrr       hhhhhhoo    ld
               rqqqqpp  dd    qn   np  rr llloo      d
              pppqa npm pqqqqpkpnj nnapm pd   l    oo
           rppddena  nkpq qrdipq hnj    n a  ld   il
        rnhh    qpreqbplrqd   qq   imkn    p d   dd
        """.Split('\n');

    private static readonly Dictionary<char, Color> Palette = new()
    {
        ['a'] = new(210, 144, 125),
        ['b'] = new(212, 88, 78),
        ['c'] = new(220, 66, 69),
        ['d'] = new(233, 211, 200),
        ['e'] = new(205, 70, 61),
        ['f'] = new(210, 46, 49),
        ['g'] = new(240, 190, 60),
        ['h'] = new(225, 180, 158),
        ['i'] = new(227, 195, 181),
        ['j'] = new(219, 116, 69),
        ['k'] = new(226, 157, 110),
        ['l'] = new(242, 228, 218),
        ['m'] = new(230, 175, 137),
        ['n'] = new(212, 141, 90),
        ['o'] = new(239, 198, 175),
        ['p'] = new(201, 163, 138),
        ['q'] = new(190, 163, 163),
        ['r'] = new(201, 198, 183),
    };
}
