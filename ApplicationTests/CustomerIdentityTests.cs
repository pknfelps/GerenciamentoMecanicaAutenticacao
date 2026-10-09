using GerenciamentoMecanica.Autenticacao.Domain.Customers;

namespace GerenciamentoMecanica.Autenticacao.ApplicationTests;

public class CustomerIdentityTests
{
    [TestCase(CustomerStatus.Active, true)]
    [TestCase(CustomerStatus.Inactive, false)]
    public void EligibilityComesFromStatus(CustomerStatus status, bool eligible) =>
        Assert.That(new CustomerIdentity(Guid.NewGuid(), "Cliente", "529.982.247-25", status).IsEligible,
            Is.EqualTo(eligible));

    [Test]
    public void EmptyIdentityAndUnknownStatusAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new CustomerIdentity(Guid.Empty, "Cliente", "529.982.247-25", CustomerStatus.Active));
        
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CustomerIdentity(Guid.NewGuid(), "Cliente", "529.982.247-25", (CustomerStatus)99));
    }

    [TestCase(null, "529.982.247-25")]
    [TestCase(" ", "529.982.247-25")]
    [TestCase("Cliente", null)]
    [TestCase("Cliente", " ")]
    [TestCase("Cliente", "52998224725")]
    [TestCase("Cliente", "529.982.247-25\n")]
    public void InvalidDataIsRejected(string? name, string? document) =>
        Assert.Catch<ArgumentException>(() =>
            new CustomerIdentity(Guid.NewGuid(), name!, document!, CustomerStatus.Active));
}