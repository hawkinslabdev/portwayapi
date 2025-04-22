using Microsoft.OData.Edm;
using Moq;
using PortwayApi.Classes;
using PortwayApi.Interfaces;
using SqlKata.Compilers;
using Xunit;

namespace PortwayApi.Tests.Services
{
    public class ODataToSqlConverterTests
    {
        private readonly ODataToSqlConverter _converter;
        private readonly Mock<IEdmModelBuilder> _mockEdmModelBuilder;
        
        public ODataToSqlConverterTests()
        {
            _mockEdmModelBuilder = new Mock<IEdmModelBuilder>();
            var compiler = new SqlServerCompiler();
            
            // Create a simple EDM model for testing
            var mockModel = new Mock<IEdmModel>();
            _mockEdmModelBuilder.Setup(m => m.GetEdmModel(It.IsAny<string>())).Returns(mockModel.Object);
            
            _converter = new ODataToSqlConverter(_mockEdmModelBuilder.Object, compiler);
        }
        
        [Fact]
        public void ConvertToSQL_BasicSelect_GeneratesCorrectSql()
        {
            // Arrange
            string entityName = "dbo.Products";
            var odataParams = new Dictionary<string, string>
            {
                { "select", "ItemCode,Description" }
            };
            
            // Act
            var (sqlQuery, parameters) = _converter.ConvertToSQL(entityName, odataParams);
            
            // Assert
            Assert.NotNull(sqlQuery);
            Assert.Contains("SELECT", sqlQuery);
            Assert.Contains("[ItemCode]", sqlQuery);
            Assert.Contains("[Description]", sqlQuery);
            Assert.Contains("FROM", sqlQuery);
            Assert.Contains("[dbo].[Products]", sqlQuery);
        }
        
        [Fact]
        public void ConvertToSQL_WithFilter_GeneratesWhereClause()
        {
            // Arrange
            string entityName = "dbo.Products";
            var odataParams = new Dictionary<string, string>
            {
                { "filter", "ItemCode eq 'TEST001'" }
            };
            
            // Act
            var (sqlQuery, parameters) = _converter.ConvertToSQL(entityName, odataParams);
            
            // Assert
            Assert.NotNull(sqlQuery);
            Assert.Contains("WHERE", sqlQuery);
            Assert.True(parameters.Count > 0);
        }
        
        [Fact]
        public void ConvertToSQL_WithOrderBy_GeneratesOrderByClause()
        {
            // Arrange
            string entityName = "dbo.Products";
            var odataParams = new Dictionary<string, string>
            {
                { "orderby", "Description desc" }
            };
            
            // Act
            var (sqlQuery, parameters) = _converter.ConvertToSQL(entityName, odataParams);
            
            // Assert
            Assert.NotNull(sqlQuery);
            Assert.Contains("ORDER BY", sqlQuery);
            Assert.Contains("DESC", sqlQuery);
        }
        
        [Fact]
        public void ConvertToSQL_WithTopAndSkip_GeneratesLimitAndOffset()
        {
            // Arrange
            string entityName = "dbo.Products";
            var odataParams = new Dictionary<string, string>
            {
                { "top", "10" },
                { "skip", "20" }
            };
            
            // Act
            var (sqlQuery, parameters) = _converter.ConvertToSQL(entityName, odataParams);
            
            // Assert
            Assert.NotNull(sqlQuery);
            // The actual SQL syntax will depend on the SQL compiler used
            // For SQL Server, expect OFFSET/FETCH or TOP/OFFSET
            Assert.True(
                sqlQuery.Contains("OFFSET") && sqlQuery.Contains("FETCH") ||
                sqlQuery.Contains("OFFSET") && sqlQuery.Contains("TOP")
            );
        }
        
        [Fact]
        public void ConvertToSQL_ComplexQuery_GeneratesFullSql()
        {
            // Arrange
            string entityName = "dbo.Products";
            var odataParams = new Dictionary<string, string>
            {
                { "select", "ItemCode,Description,Price" },
                { "filter", "Price gt 100" },
                { "orderby", "Price desc" },
                { "top", "5" },
                { "skip", "10" }
            };
            
            // Act
            var (sqlQuery, parameters) = _converter.ConvertToSQL(entityName, odataParams);
            
            // Assert
            Assert.NotNull(sqlQuery);
            Assert.Contains("SELECT", sqlQuery);
            Assert.Contains("WHERE", sqlQuery);
            Assert.Contains("ORDER BY", sqlQuery);
            Assert.True(parameters.Count > 0);
        }
    }
}