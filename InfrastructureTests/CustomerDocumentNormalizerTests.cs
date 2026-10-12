using GerenciamentoMecanica.Autenticacao.Infrastructure.Documents;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

public class CustomerDocumentNormalizerTests
{
    [TestCase("52998224725", "529.982.247-25")]
    [TestCase("529.982.247-25", "529.982.247-25")]
    [TestCase("10359666000194", "10.359.666/0001-94")]
    [TestCase("10.359.666/0001-94", "10.359.666/0001-94")]
    [TestCase("00000000000", "000.000.000-00")]
    [TestCase("00000000000000", "00.000.000/0000-00")]
    public void UsesSharedCanonicalFormatAndLegacyChecksums(string input, string canonical) =>
        Assert.That(new CustomerDocumentNormalizer().Normalize(input), Is.EqualTo(canonical));

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("52998224724")]
    [TestCase("10359666000195")]
    [TestCase("1035966600019")]
    [TestCase("52998224725 ")]
    [TestCase(" 52998224725")]
    [TestCase("52998224725\n")]
    [TestCase("52998224725\t")]
    [TestCase("529!982!247!25")]
    [TestCase("529-982-247.25")]
    [TestCase("529.982247-25")]
    [TestCase("10.359.666-0001/94")]
    [TestCase("x52998224725")]
    [TestCase("５２９９８２２４７２５")]
    [TestCase("52998224725;DROP TABLE customers")]
    public void RejectsInputBeforePermissiveSharedNormalization(string? input) =>
        Assert.That(new CustomerDocumentNormalizer().Normalize(input), Is.Null);
}