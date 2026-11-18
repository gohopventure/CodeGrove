namespace Codegrove.CSharpRunner;

/// <summary>
/// Plain-language versions of the compiler errors and exceptions beginners meet most. The real
/// message is always shown as well (Builders are learning to read them), with the code in brackets
/// so it can be searched. Explorers and Builders get the same wording; it's written for the younger band.
/// </summary>
public static class FriendlyErrors
{
    private static readonly Dictionary<string, string> Compiler = new()
    {
        ["CS1002"] = "C# expected a semicolon ; to end this statement. Most lines of C# end with one.",
        ["CS1003"] = "Something is missing here, often a comma or a bracket. Compare this line with the example.",
        ["CS1010"] = "A piece of text is missing its closing quote \". Text (a string) needs a \" at both ends.",
        ["CS1012"] = "Too many characters in single quotes. Use double quotes \" for text; single quotes are for one character.",
        ["CS1026"] = "A closing round bracket ) is missing. Every ( needs a matching ).",
        ["CS1513"] = "A closing curly brace } is missing. Every { needs a matching }.",
        ["CS1514"] = "An opening curly brace { is missing.",
        ["CS1022"] = "There's an extra closing curly brace }, or code outside the class it belongs to.",
        ["CS1525"] = "C# didn't expect this here. Check for a missing value or an extra symbol.",
        ["CS1001"] = "C# expected a name here (an identifier), like a variable or method name.",
        ["CS0103"] = "C# doesn't know this name. Check the spelling and capital letters (C# cares about both), and that it was created before this line.",
        ["CS0117"] = "That thing doesn't have a member with this name. Check the spelling and capital letters.",
        ["CS1061"] = "That value doesn't have a method or property with this name. Check the spelling and capital letters.",
        ["CS0246"] = "C# can't find a type with this name. Check the spelling and capital letters.",
        ["CS0029"] = "This value is the wrong type for where you're putting it, like text where a number should go.",
        ["CS0266"] = "This value might not fit the type you're putting it in, so C# won't convert it on its own.",
        ["CS0019"] = "This operator can't be used with these two kinds of values.",
        ["CS0128"] = "There's already a variable with this name here. Give it a different name, or leave off the type to change the old one.",
        ["CS0165"] = "This variable is used before it has been given a value.",
        ["CS0161"] = "This method promises to return a value, but some way through it doesn't reach a return.",
        ["CS0127"] = "This method is void, so it can't return a value.",
        ["CS0120"] = "This needs an object to work on. Create one with new, or mark the member static.",
        ["CS1501"] = "This method doesn't take that many arguments (the values in brackets).",
        ["CS7036"] = "This needs more arguments (values in brackets) than you gave it.",
        ["CS1503"] = "One of the arguments is the wrong type for this method.",
        ["CS0136"] = "A variable with this name already exists in an outer part of the code. Pick another name.",
        ["CS0201"] = "This line doesn't do anything on its own. Did you mean to store it in a variable, or call a method?",
        ["CS0023"] = "This operator can't be used on that kind of value.",
        ["CS0122"] = "This member is private, so code outside its class can't use it.",
        ["CS0501"] = "This method needs a body: put its code between { and }.",
        ["CS0106"] = "This keyword isn't allowed here.",
        ["CS0116"] = "This code needs to be inside a class or method.",
        ["CS8803"] = "Top-level statements (your program's main code) must come before any class.",
        ["CS5001"] = "There's no program to run. Write some statements at the top of Program.cs.",
        ["CS0234"] = "C# can't find that name inside this namespace. Check the spelling.",
        ["CS0131"] = "The left side of = must be a variable, property or array item, something that can hold a value.",
        ["CS0170"] = "This field is used before it has a value.",
        ["CS0236"] = "A field can't use another field to set its starting value. Do it in a constructor instead.",
    };

    private static readonly Dictionary<string, string> Runtime = new()
    {
        ["NullReferenceException"] = "Something was null (empty, no object), and the program tried to use it.",
        ["IndexOutOfRangeException"] = "The program asked for an item past the end of an array. Remember the first item is number 0.",
        ["ArgumentOutOfRangeException"] = "The program asked for a position that doesn't exist, like an item past the end of a list. The first item is number 0.",
        ["DivideByZeroException"] = "The program tried to divide by zero, which has no answer.",
        ["FormatException"] = "The program tried to turn text into a number (or another type), but the text wasn't in the right format.",
        ["InvalidCastException"] = "The program tried to treat a value as a type it isn't.",
        ["KeyNotFoundException"] = "The program looked up a key that isn't in the dictionary.",
        ["InvalidOperationException"] = "The program did something that isn't allowed right now, like taking the first item of an empty list.",
        ["OverflowException"] = "A number got too big (or too small) for its type.",
        ["ArgumentException"] = "A method was given a value it can't work with.",
        ["ArgumentNullException"] = "A method was given null where it needs a real value.",
        ["NotImplementedException"] = "This part of the program hasn't been written yet.",
    };

    public static string ForCompilerError(string code) => Compiler.GetValueOrDefault(code) is { Length: > 0 } s ? s : "";

    public static string ForException(string typeName) => Runtime.GetValueOrDefault(typeName) ?? "";
}
