using System;
namespace DualTarget
{
    public class MyException : Exception
    {
        public MyException(string message) : base(message)
        {
        }
    }

    public class ExceptionFlow
    {
        public int Guard(int value)
        {
            try
            {
                if (value < 0)
                {
                    throw new MyException("negative");
                }
                if (value == 0)
                {
                    throw new DivideByZeroException();
                }
                return 100 / value;
            }
            catch (MyException)
            {
                return -1;
            }
            catch (Exception)
            {
                return -2;
            }
            finally
            {
                value = value;
            }
        }
    }
}
