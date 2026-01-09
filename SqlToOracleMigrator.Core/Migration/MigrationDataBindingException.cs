    using System;
    using System.Collections.Generic;
    using System.Text;

    namespace SqlToOracleMigrator.Core.Migration
    {
        public sealed class MigrationDataBindingException : Exception
        {
            public string Stage { get; }
            public string Schema { get; }
            public string ObjectName { get; }
            public long RowNumber { get; }
            public int BatchNumber { get; }
            public int BatchRowIndex { get; }
            public string SourceColumn { get; }
            public string TargetColumn { get; }
            public string OracleParameterName { get; }
            public string OracleDbType { get; }
            public int? Size { get; }
            public byte? Precision { get; }
            public byte? Scale { get; }
            public string ValueType { get; }
            public string ValuePreview { get; }

            public MigrationDataBindingException(
                string stage,
                string schema,
                string objectName,
                long rowNumber,
                int batchNumber,
                int batchRowIndex,
                string sourceColumn,
                string targetColumn,
                string oracleParameterName,
                string oracleDbType,
                int? size,
                byte? precision,
                byte? scale,
                string valueType,
                string valuePreview,
                Exception inner)
                : base(inner?.Message ?? "Data binding failed.", inner)
            {
                Stage = stage ?? "";
                Schema = schema ?? "";
                ObjectName = objectName ?? "";
                RowNumber = rowNumber;
                BatchNumber = batchNumber;
                BatchRowIndex = batchRowIndex;
                SourceColumn = sourceColumn ?? "";
                TargetColumn = targetColumn ?? "";
                OracleParameterName = oracleParameterName ?? "";
                OracleDbType = oracleDbType ?? "";
                Size = size;
                Precision = precision;
                Scale = scale;
                ValueType = valueType ?? "";
                ValuePreview = valuePreview ?? "";
            }

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendLine(base.ToString());
                sb.AppendLine("---- Data Binding Context ----");
                sb.AppendLine($"Stage={Stage} Schema={Schema} Object={ObjectName} RowNumber={RowNumber} Batch={BatchNumber} BatchRowIndex={BatchRowIndex}");
                sb.AppendLine($"SourceColumn={SourceColumn} TargetColumn={TargetColumn} Param={OracleParameterName} OracleDbType={OracleDbType} Size={Size} Precision={Precision} Scale={Scale}");
                sb.AppendLine($"ValueType={ValueType} ValuePreview={ValuePreview}");
                return sb.ToString();
            }

            public Dictionary<string, object?> ToDiagnosticJson()
            {
                return new Dictionary<string, object?>
                {
                    ["stage"] = Stage,
                    ["schema"] = Schema,
                    ["object"] = ObjectName,
                    ["rowNumber"] = RowNumber,
                    ["batchNumber"] = BatchNumber,
                    ["batchRowIndex"] = BatchRowIndex,
                    ["sourceColumn"] = SourceColumn,
                    ["targetColumn"] = TargetColumn,
                    ["oracleParameter"] = OracleParameterName,
                    ["oracleDbType"] = OracleDbType,
                    ["size"] = Size,
                    ["precision"] = Precision,
                    ["scale"] = Scale,
                    ["valueType"] = ValueType,
                    ["valuePreview"] = ValuePreview,
                    ["exceptionType"] = InnerException?.GetType().FullName ?? GetType().FullName,
                    ["message"] = InnerException?.Message ?? Message,
                    ["timestampUtc"] = DateTime.UtcNow.ToString("o")
                };
            }
        }
    }
