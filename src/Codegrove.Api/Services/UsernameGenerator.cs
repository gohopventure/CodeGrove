using System.Security.Cryptography;

namespace Codegrove.Api.Services;

/// <summary>
/// Suggests safe usernames (adjective + animal + 2 digits) so Explorers don't have to invent one
/// and are less tempted to use their real name.
/// </summary>
public static class UsernameGenerator
{
    private static readonly string[] Adjectives =
    [
        "Brave", "Clever", "Cosmic", "Curious", "Daring", "Electric", "Fuzzy", "Galactic", "Happy", "Jolly",
        "Lucky", "Mighty", "Nimble", "Pixel", "Quick", "Rapid", "Sunny", "Swift", "Turbo", "Zippy",
    ];

    private static readonly string[] Animals =
    [
        "Badger", "Beaver", "Falcon", "Ferret", "Fox", "Gecko", "Koala", "Lemur", "Lynx", "Narwhal",
        "Otter", "Owl", "Panda", "Penguin", "Puffin", "Raccoon", "Robin", "Sloth", "Tiger", "Yak",
    ];

    public static string Next() =>
        $"{Adjectives[RandomNumberGenerator.GetInt32(Adjectives.Length)]}" +
        $"{Animals[RandomNumberGenerator.GetInt32(Animals.Length)]}" +
        $"{RandomNumberGenerator.GetInt32(10, 100)}";
}
