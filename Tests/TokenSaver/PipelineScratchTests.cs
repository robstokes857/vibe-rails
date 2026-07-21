using System.Buffers;
using TokenSaver.Pipeline;
using Xunit;

namespace Tests.TokenSaver;

public sealed class PipelineScratchTests
{
    [Fact]
    public void Constructor_ReturnsFirstRental_WhenSecondRentalFails()
    {
        var pool = new FailingSecondRentPool();

        Assert.Throws<InvalidOperationException>(() => new PipelineScratch(32, pool));

        Assert.Equal(1, pool.ReturnCount);
        Assert.Same(pool.FirstRental, pool.ReturnedArray);
    }

    private sealed class FailingSecondRentPool : ArrayPool<char>
    {
        private int _rentCount;

        public char[] FirstRental { get; } = new char[32];

        public int ReturnCount { get; private set; }

        public char[]? ReturnedArray { get; private set; }

        public override char[] Rent(int minimumLength)
        {
            if (++_rentCount == 2)
            {
                throw new InvalidOperationException("Simulated pool exhaustion.");
            }

            return FirstRental;
        }

        public override void Return(char[] array, bool clearArray = false)
        {
            ReturnCount++;
            ReturnedArray = array;
        }
    }
}
