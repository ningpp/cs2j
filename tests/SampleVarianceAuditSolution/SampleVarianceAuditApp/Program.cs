using SampleVarianceAuditApp;
using SampleVarianceAuditLib;

if (VarianceUses.Read(new StringProducer("x")) != "x") return 1;
if ((string)VarianceUses.CovariantAssignment() != "value") return 2;
if (VarianceUses.ArrayCovariance() != 2) return 3;
if (VarianceUses.Zero<int>() != 0) return 4;
VarianceUses.ContravariantArgument();
VarianceUses.MethodGroupArgument();
if (VarianceUses.ReadOnlyView().Count != 0) return 5;
if (ResidualConstruction.Build<NamedThing>().Name != "built") return 6;

var box = new Box<int>(7);
if (box.Value != 7) return 7;

var consumer = new StringConsumer();
consumer.Consume("seen");
if (consumer.Count != 1) return 8;

return 0;