using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SshNotepad;

/// <summary>Windows' own spell checker (Windows 8+). Does nothing if it has no dictionary for the language.</summary>
static class Spelling
{
    private static readonly ISpellChecker? Checker = Create();

    public static bool Available => Checker is not null;

    public static bool IsWrong(string word) => First(word) is not null;

    public static IReadOnlyList<(int Start, int Length)> Check(string text)
    {
        var found = new List<(int, int)>();
        try
        {
            if (Checker is null || Checker.Check(text, out var errors) < 0 || errors is null) return found;
            while (errors.Next(out var error) == 0)
            {
                error.StartIndex(out uint start);
                error.Length(out uint length);
                found.Add(((int)start, (int)length));
            }
        }
        catch (COMException) { }
        return found;
    }

    // Windows' checker offers no automatic replacements of its own, so: common English typos, none of them real words.
    private static readonly Dictionary<string, string> Typos = (
        "teh:the hte:the adn:and taht:that thsi:this waht:what wiht:with whihc:which thier:their recieve:receive recieved:received "
        + "beleive:believe acheive:achieve freind:friend wierd:weird untill:until occured:occurred occuring:occurring seperate:separate "
        + "seperately:separately definately:definitely accomodate:accommodate neccessary:necessary necesary:necessary begining:beginning "
        + "tommorow:tomorrow tommorrow:tomorrow tomorow:tomorrow becuase:because becasue:because beacuse:because "
        + "enviroment:environment goverment:government independant:independent succesful:successful sucess:success truely:truly wich:which "
        + "youre:you're dont:don't doesnt:doesn't didnt:didn't isnt:isn't wasnt:wasn't arent:aren't couldnt:couldn't shouldnt:shouldn't "
        + "wouldnt:wouldn't havent:haven't hasnt:hasn't thats:that's theres:there's alot:a_lot jsut:just knwo:know liek:like")
        .Split(' ').Select(p => p.Split(':')).ToDictionary(p => p[0], p => p[1].Replace('_', ' '));

    /// <summary>The fix for a common typo, keeping a leading capital.</summary>
    public static string? AutoCorrection(string word)
    {
        if (!Typos.TryGetValue(word.ToLowerInvariant(), out string? fix)) return null;
        if (word.All(char.IsLower)) return fix;
        return char.IsUpper(word[0]) && word.Skip(1).All(char.IsLower) ? char.ToUpperInvariant(fix[0]) + fix[1..] : null;
    }

    public static IReadOnlyList<string> Suggest(string word, int max)
    {
        var found = new List<string>();
        try
        {
            if (Checker is null || Checker.Suggest(word, out var list) < 0 || list is null) return found;
            var one = new string[1];
            while (found.Count < max && list.Next(1, one, 0) == 0) if (one[0] != word) found.Add(one[0]);
        }
        catch (COMException) { }
        return found;
    }

    public static void Add(string word) { try { Checker?.Add(word); } catch (COMException) { } }
    public static void Ignore(string word) { try { Checker?.Ignore(word); } catch (COMException) { } }

    private static ISpellingError? First(string word)
    {
        try
        {
            if (Checker is null || Checker.Check(word, out var errors) < 0 || errors is null) return null;
            return errors.Next(out var error) == 0 ? error : null;
        }
        catch (COMException) { return null; }
    }

    private static ISpellChecker? Create()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"));
            if (type is null || Activator.CreateInstance(type) is not ISpellCheckerFactory factory) return null;
            foreach (string language in (string[])[CultureInfo.CurrentUICulture.Name, "en-US"])
                if (factory.IsSupported(language, out int ok) == 0 && ok != 0 && factory.CreateSpellChecker(language, out var checker) == 0) return checker;
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { }
        return null;
    }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        [PreserveSig] int SupportedLanguages(out IEnumString value);
        [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string language, out int value);
        [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string language, out ISpellChecker value);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [PreserveSig] int LanguageTag([MarshalAs(UnmanagedType.LPWStr)] out string value);
        [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError value);
        [PreserveSig] int Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out IEnumString value);
        [PreserveSig] int Add([MarshalAs(UnmanagedType.LPWStr)] string word);
        [PreserveSig] int Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError { [PreserveSig] int Next(out ISpellingError value); }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        [PreserveSig] int StartIndex(out uint value);
        [PreserveSig] int Length(out uint value);
        [PreserveSig] int CorrectiveAction(out int value);
        [PreserveSig] int Replacement([MarshalAs(UnmanagedType.LPWStr)] out string value);
    }
}
