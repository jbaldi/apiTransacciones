using ApiTransacciones.Domain;
using Xunit;

public class PaymentStateMachineTests
{
    [Theory]
    [InlineData(PaymentState.Pendiente, PaymentState.EnProceso)]
    [InlineData(PaymentState.EnProceso, PaymentState.Pagado)]
    [InlineData(PaymentState.EnProceso, PaymentState.Fallido)]
    [InlineData(PaymentState.EnProceso, PaymentState.Incierto)]
    [InlineData(PaymentState.Incierto, PaymentState.Pagado)]
    [InlineData(PaymentState.Incierto, PaymentState.Fallido)]
    public void TransicionesValidas_SonPermitidas(string from, string to)
        => Assert.True(PaymentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(PaymentState.Pagado, PaymentState.Pendiente)]
    [InlineData(PaymentState.Pagado, PaymentState.Fallido)]
    [InlineData(PaymentState.Fallido, PaymentState.Pagado)]
    [InlineData(PaymentState.Pendiente, PaymentState.Pagado)] // no se puede pagar sin pasar por EN_PROCESO
    public void TransicionInvalida_EsRechazada(string from, string to)
    {
        Assert.False(PaymentStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => PaymentStateMachine.EnsureTransition(from, to));
    }
}
