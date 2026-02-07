using VibeRails.Services.VCA;
using Xunit;

namespace Tests.Services.VCA
{
    public class FileClassifierTests
    {
        private readonly FileClassifier _classifier;

        public FileClassifierTests()
        {
            _classifier = new FileClassifier();
        }

        [Theory]
        [InlineData("test.cs", true)]
        [InlineData("test.js", true)]
        [InlineData("test.ts", true)]
        [InlineData("test.py", true)]
        [InlineData("test.java", true)]
        [InlineData("test.txt", false)]
        [InlineData("test.md", false)]
        [InlineData("test.json", false)]
        public void IsCodeFile_ShouldIdentifyCodeFiles(string fileName, bool expected)
        {
            // Act
            var result = _classifier.IsCodeFile(fileName);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("MyTest.cs", true)]
        [InlineData("MyTests.cs", true)]
        [InlineData("my.test.js", true)]
        [InlineData("my.spec.ts", true)]
        [InlineData("/src/test/MyClass.cs", true)]
        [InlineData("/src/tests/MyClass.cs", true)]
        [InlineData("\\src\\test\\MyClass.cs", true)]
        [InlineData("MyClass.cs", false)]
        [InlineData("Testimony.cs", false)]
        public void IsTestFile_ShouldIdentifyTestFiles(string fileName, bool expected)
        {
            // Act
            var result = _classifier.IsTestFile(fileName);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("package.json", true)]
        [InlineData("package-lock.json", true)]
        [InlineData("requirements.txt", true)]
        [InlineData("MyProject.csproj", true)]
        [InlineData("MyProject.fsproj", true)]
        [InlineData("pom.xml", true)]
        [InlineData("Cargo.toml", true)]
        [InlineData("go.mod", true)]
        [InlineData("MyClass.cs", false)]
        [InlineData("readme.md", false)]
        public void IsPackageFile_ShouldIdentifyPackageFiles(string fileName, bool expected)
        {
            // Act
            var result = _classifier.IsPackageFile(fileName);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("test.cs", true)]
        [InlineData("test.js", true)]
        [InlineData("test.ts", true)]
        [InlineData("test.py", false)]
        [InlineData("test.java", false)]
        [InlineData("test.txt", false)]
        public void IsComplexityCheckable_ShouldIdentifyCheckableFiles(string fileName, bool expected)
        {
            // Act
            var result = _classifier.IsComplexityCheckable(fileName);

            // Assert
            Assert.Equal(expected, result);
        }
    }
}
