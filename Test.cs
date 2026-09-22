
public class Outer
{
    protected enum State { A }
    internal sealed class Inner
    {
        protected const State C = 0;
    }
}
