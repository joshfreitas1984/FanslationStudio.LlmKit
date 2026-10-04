using FanslationStudio.LlmKit;

namespace FanslationStudio.LlmKit.Tests;

public class PronounRepairTests
{
    [Theory(DisplayName = "PronounRepair rewrites he/she/his/her/him to they/their/them and fixes the verb")]
    [InlineData("He also performs rituals.", "They also perform rituals.")]
    [InlineData("Instinctively, he knew this was an opponent he could not defeat.", "Instinctively, they knew this was an opponent they could not defeat.")]
    [InlineData("The moment Yu Lin saw this person, his hair stood on end.", "The moment Yu Lin saw this person, their hair stood on end.")]
    [InlineData("She is quick, and she was there.", "They are quick, and they were there.")]
    [InlineData("Send this little beggar on his way.", "Send this little beggar on their way.")]
    [InlineData("She hates no one more than him.", "They hate no one more than them.")]
    [InlineData("Seeing him in such a state, I spared him.", "Seeing them in such a state, I spared them.")]
    [InlineData("He washes himself.", "They wash themselves.")]
    [InlineData("He carries his tools.", "They carry their tools.")]
    [InlineData("He has a plan.", "They have a plan.")]
    [InlineData("They said she's tired.", "They said they're tired.")]
    public void Neutralise_Rewrites(string input, string expected) =>
        Assert.Equal(expected, PronounRepair.Neutralise(input));

    [Theory(DisplayName = "PronounRepair gives up (null) when a second present-tense verb or plural noun follows and it cannot tell them apart")]
    [InlineData("He washes himself and goes home.")]
    [InlineData("He picks up the bow and arrows.")]
    [InlineData("He has a plan but does not share it.")]
    public void Neutralise_ChainedVerbs_GivesUp(string input) =>
        Assert.Null(PronounRepair.Neutralise(input));

    [Theory(DisplayName = "PronounRepair treats her as their before a noun and them before a preposition or the end")]
    [InlineData("She took her sword.", "They took their sword.")]
    [InlineData("They helped her up.", "They helped them up.")]
    [InlineData("They helped her.", "They helped them.")]
    public void Neutralise_Her(string input, string expected) =>
        Assert.Equal(expected, PronounRepair.Neutralise(input));

    [Theory(DisplayName = "PronounRepair leaves a name ending in He alone and returns null when there is nothing to do")]
    [InlineData("His name is Chao He.", "Their name is Chao He.")]
    [InlineData("This person is Chao He.", null)]
    [InlineData("Nothing gendered here.", null)]
    public void Neutralise_NamesAndNoops(string input, string? expected) =>
        Assert.Equal(expected, PronounRepair.Neutralise(input));
}
