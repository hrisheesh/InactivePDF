using InactivePDF.Application.Policies;

namespace InactivePDF.UnitTests.Resources;

public sealed class FileNamePolicyTests
{
    [Theory]
    [InlineData("document.docx")]
    [InlineData("photo 01.jpg")]
    public void SafeNamesAreAccepted(string name)
    {
        Assert.Equal(name, new FileNamePolicy().ValidateAndNormalize(name));
    }

    [Theory]
    [InlineData("../document.docx")]
    [InlineData("folder/document.docx")]
    [InlineData("..")]
    public void TraversalNamesAreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new FileNamePolicy().ValidateAndNormalize(name));
    }
}
