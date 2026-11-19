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
        ["CS0117"] = "This class has nothing with that name. Check the spelling and capital letters, or add it if it doesn't exist yet.",
        ["CS1061"] = "That value has no method or property with this name. Check the spelling and capital letters, or add it if it doesn't exist yet.",
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
        ["CS7036"] = "This needs more arguments (values in brackets) than you gave it. In a derived class's constructor, pass them to the base class with : base(...) after the constructor's brackets.",
        ["CS1503"] = "One of the arguments is the wrong type for this method.",
        ["CS0136"] = "A variable with this name already exists in an outer part of the code. Pick another name.",
        ["CS0201"] = "This line doesn't do anything on its own. Did you mean to store it in a variable, or call a method?",
        ["CS0023"] = "This operator can't be used on that kind of value.",
        ["CS0122"] = "This member is private (or protected), so code here can't use it.",
        ["CS0501"] = "This method needs a body: put its code between { and }.",
        ["CS0106"] = "This keyword isn't allowed here.",
        ["CS0116"] = "This code needs to be inside a class or method.",
        ["CS8803"] = "Top-level statements (your program's main code) must come before any class or record. Move classes and records to the bottom of the file.",
        ["CS5001"] = "There's no program to run. Write some statements at the top of Program.cs.",
        ["CS0234"] = "C# can't find that name inside this namespace. Check the spelling.",
        ["CS0131"] = "The left side of = must be a variable, property or array item, something that can hold a value.",
        ["CS0170"] = "This field is used before it has a value.",
        ["CS0236"] = "A field can't use another field to set its starting value. Do it in a constructor instead.",
        ["CS0163"] = "Each case in a switch must end with break (or return), so the code can't run on into the next case.",
        ["CS0152"] = "Two cases in this switch check the same value. Each case needs a different one.",
        ["CS8510"] = "This pattern can never match: an earlier one already catches every value it would. Put narrower patterns first.",
        ["CS8120"] = "This case can never match: an earlier case already catches every value it would. Put narrower cases first.",
        ["CS8509"] = "This switch expression doesn't handle every possible value. Add a final arm with _ to catch the rest.",
        ["CS0160"] = "An earlier catch already catches this kind of exception, so this catch could never run. Put the more specific catch first.",
        ["CS1524"] = "A try block needs a catch block (or a finally block) straight after it.",
        // Inheritance (csharp-objects lesson 2).
        ["CS0114"] = "This method has the same name as one in the base class, but it hides it instead of replacing it. Add override if you meant to replace it.",
        ["CS0108"] = "This has the same name as a member of the base class, so it hides that member. Rename it, or use override if the base member is virtual.",
        ["CS0115"] = "There's no virtual or abstract method with this name in the base class for override to replace. Check the name, the parameters and the base class.",
        ["CS0506"] = "This method in the base class isn't virtual, so it can't be overridden. The base class must mark it virtual (or abstract).",
        ["CS0534"] = "This class doesn't override an abstract method from its base class. Every abstract method needs an override.",
        ["CS0144"] = "This is abstract (or an interface), so you can't create one with new. Create one of the classes that derive from it or implement it instead.",
        ["CS0509"] = "This class is sealed, so no class can derive from it.",
        ["CS0513"] = "Only an abstract class can have abstract methods. Mark the class abstract, or give the method a body.",
        ["CS0621"] = "A virtual or abstract method can't be private, because derived classes couldn't see it to override it.",
        ["CS0272"] = "This property's set is private or protected, so code here can only read it.",
        // Interfaces (csharp-objects lesson 3).
        ["CS0535"] = "This class says it implements an interface, but it's missing one of the interface's members. Add every member the interface lists.",
        ["CS0737"] = "This method has the right name for the interface, but it isn't public, so it doesn't count. Interface members must be public.",
        ["CS0738"] = "This method has the right name for the interface, but its return type doesn't match the interface. Use the same type the interface uses.",
        ["CS1722"] = "The base class has to come first in the list after the colon, before any interfaces.",
        ["CS0527"] = "Only one class can come after the colon. The other names in the list must be interfaces.",
        // Records (csharp-objects lesson 4).
        ["CS8852"] = "This property can only be set while the object is being created. To change a record, make a copy with with { ... }.",
        ["CS0200"] = "This property is read-only, so it can't be given a new value here.",
        ["CS8851"] = "This record writes its own Equals but not GetHashCode. Write both, so equal records get equal hash codes.",
        ["CS8872"] = "A record's own Equals method must be virtual (unless the record is sealed): write public virtual bool Equals(...).",
        ["CS8858"] = "with only works on records (and structs). Make this type a record to copy it with with { ... }.",
        ["CS0111"] = "This type already has a member with this name and the same parameter types. (A record writes Equals(object) for you: write Equals(YourRecord other) instead.)",
        // Nullable reference types (csharp-objects lesson 6). These are warnings: the program still runs.
        ["CS8600"] = "This value might be null, but it's going into a variable that says it's never null. Make the variable's type nullable (string?), or give a fallback with ??.",
        ["CS8601"] = "This might put null somewhere that says it's never null. Check for null first, or make that type nullable with ?.",
        ["CS8602"] = "This might be null here, and using a member of null crashes. Check for null first, or use ?. and ?? to handle it.",
        ["CS8603"] = "This method might return null, but its return type says it never does. Add ? to the return type, or return something that isn't null.",
        ["CS8604"] = "This argument might be null, but the method says it needs a real value. Check for null first, or pass a fallback with ??.",
        ["CS8618"] = "This non-nullable property or field is never given a value when the object is created, so it starts as null. Make it nullable with ? if it's optional, mark it required, or give it a value.",
        ["CS8625"] = "null can't go here: this type says it's never null. Make the type nullable with ? if null is allowed.",
        ["CS8629"] = "This nullable value might be null here. Check HasValue (or for null) first, or use ?? to give a fallback.",
        ["CS8632"] = "The ? on this type only works where nullable checking is on. Add #nullable enable at the top of this file.",
        ["CS9035"] = "This property is required, so it has to be set when the object is created: add it inside the { } after new.",
        ["CS0155"] = "Only exceptions can be thrown or caught: types that derive from Exception.",
    };

    private static readonly Dictionary<string, string> Runtime = new()
    {
        ["NullReferenceException"] = "Something was null (no object at all), and the program tried to use it.",
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

    /// <summary>Plain words for a compiler error. The message picks a better text for a few common special cases.</summary>
    private static readonly System.Text.RegularExpressions.Regex InterfaceConversion =
        new(@"convert type '(\w+)' to '(I[A-Z]\w*)'");

    public static string ForCompilerError(string code, string message = "")
    {
        // `if (x = 10)`: one = assigns, so the condition is an int, not a bool.
        if (code == "CS0029" && message.EndsWith("to 'bool'", StringComparison.Ordinal))
            return "A condition must be true or false. To compare two values, use == (two equals signs); one = puts a value into a variable.";
        // `IGlow[] parade = { new Mushroom() }` where Mushroom forgot `: IGlow`. The message alone
        // doesn't say the target is an interface, so this relies on the I-prefix naming convention.
        var toInterface = InterfaceConversion.Match(message);
        if ((code == "CS0266" || code == "CS0029") && toInterface.Success)
            return $"{toInterface.Groups[1].Value} doesn't implement {toInterface.Groups[2].Value}, so it can't go where {toInterface.Groups[2].Value} is needed. Add it after a colon: class {toInterface.Groups[1].Value} : {toInterface.Groups[2].Value}.";
        return Compiler.GetValueOrDefault(code) ?? "";
    }

    public static string ForException(string typeName) => Runtime.GetValueOrDefault(typeName) ?? "";
}
