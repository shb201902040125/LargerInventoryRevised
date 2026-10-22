using System;

namespace LargerInventory.Backend.Exceptions;

public class UnreachableException:Exception
{
    public  UnreachableException(string message) : base(message)
    {
    }

    public override string ToString()
    {
        return
            "The code has reached a situation that should not have occurred in the branch." +
            " This means that there were unexpected external modifications during the logical execution process.";
    }
}