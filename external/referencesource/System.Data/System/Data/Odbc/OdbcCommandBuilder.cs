//------------------------------------------------------------------------------
// <copyright file="OdbcCommandBuilder.cs" company="Microsoft">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// <owner current="true" primary="true">[....]</owner>
// <owner current="true" primary="false">[....]</owner>
//------------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Data.ProviderBase;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Globalization;
using System.Text;


namespace System.Data.Odbc {

    public sealed class OdbcCommandBuilder : DbCommandBuilder {

        public OdbcCommandBuilder() : base() {
            GC.SuppressFinalize(this);
        }

        public OdbcCommandBuilder(OdbcDataAdapter adapter) : this() {
            DataAdapter = adapter;
        }

        [
        DefaultValue(null),
        ResCategoryAttribute(Res.DataCategory_Update),
        ResDescriptionAttribute(Res.OdbcCommandBuilder_DataAdapter), // MDAC 60524
        ]
        new public OdbcDataAdapter DataAdapter {
            get {
                return (base.DataAdapter as OdbcDataAdapter);
            }
            set {
                base.DataAdapter = value;
            }
        }

        private void OdbcRowUpdatingHandler(object sender, OdbcRowUpdatingEventArgs ruevent) {
            RowUpdatingHandler(ruevent);
        }

        new public OdbcCommand GetInsertCommand() {
            return (OdbcCommand) base.GetInsertCommand();
        }
        new public OdbcCommand GetInsertCommand(bool useColumnsForParameterNames) {
            return (OdbcCommand) base.GetInsertCommand(useColumnsForParameterNames);
        }

        new public OdbcCommand GetUpdateCommand() {
            return (OdbcCommand) base.GetUpdateCommand();
        }
        new public OdbcCommand GetUpdateCommand(bool useColumnsForParameterNames) {
            return (OdbcCommand) base.GetUpdateCommand(useColumnsForParameterNames);
        }

        new public OdbcCommand GetDeleteCommand() {
            return (OdbcCommand) base.GetDeleteCommand();
        }
        new public OdbcCommand GetDeleteCommand(bool useColumnsForParameterNames) {
            return (OdbcCommand) base.GetDeleteCommand(useColumnsForParameterNames);
        }

        override protected string GetParameterName(int parameterOrdinal) {
            return "p" + parameterOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        override protected string GetParameterName(string parameterName) {
            return parameterName;
        }

        override protected string GetParameterPlaceholder(int parameterOrdinal) {
            return "?";
        }

        override protected void ApplyParameterInfo(DbParameter parameter, DataRow datarow, StatementType statementType, bool whereClause) {
            OdbcParameter p = (OdbcParameter) parameter;
            object valueType = datarow[SchemaTableColumn.ProviderType];
            p.OdbcType = (OdbcType) valueType;

            object bvalue = datarow[SchemaTableColumn.NumericPrecision];
            if (DBNull.Value != bvalue) {
                byte bval = (byte)(short)bvalue;
                p.PrecisionInternal = ((0xff != bval) ? bval : (byte)0);
            }

            bvalue = datarow[SchemaTableColumn.NumericScale];
            if (DBNull.Value != bvalue) {
                byte bval = (byte)(short)bvalue;
                p.ScaleInternal = ((0xff != bval) ? bval : (byte)0);
            }
        }

        static public void DeriveParameters(OdbcCommand command) {
            // MDAC 65927
            OdbcConnection.ExecutePermission.Demand();

            if (null == command) {
                throw ADP.ArgumentNull("command");
            }
            switch (command.CommandType) {
                case System.Data.CommandType.Text:
                    throw ADP.DeriveParametersNotSupported(command);
                case System.Data.CommandType.StoredProcedure:
                    break;
                case System.Data.CommandType.TableDirect:
                    // CommandType.TableDirect - do nothing, parameters are not supported
                    throw ADP.DeriveParametersNotSupported(command);
                default:
                    throw ADP.InvalidCommandType(command.CommandType);
            }
            if (ADP.IsEmpty(command.CommandText)) {
                throw ADP.CommandTextRequired(ADP.DeriveParameters);
            }

            OdbcConnection connection = command.Connection;

            if (null == connection) {
                throw ADP.ConnectionRequired(ADP.DeriveParameters);
            }

            ConnectionState state = connection.State;

            if (ConnectionState.Open != state) {
                throw ADP.OpenConnectionRequired(ADP.DeriveParameters, state);
            }

            OdbcParameter[] list = DeriveParametersFromStoredProcedure(connection, command);

            OdbcParameterCollection parameters = command.Parameters;
            parameters.Clear();

            int count = list.Length;
            if (0 < count) {
                for(int i = 0; i < list.Length; ++i) {
                    parameters.Add(list[i]);
                }
            }
            }


        // DeriveParametersFromStoredProcedure (
        //  OdbcConnection connection,
        //  OdbcCommand command);
        //
        // Uses SQLProcedureColumns to create an array of OdbcParameters
        //

        static private OdbcParameter[] DeriveParametersFromStoredProcedure(OdbcConnection connection, OdbcCommand command) {
            List<OdbcParameter> rParams = new List<OdbcParameter>();

            // following call ensures that the command has a statement handle allocated
            CMDWrapper cmdWrapper = command.GetStatementHandle();
            OdbcStatementHandle hstmt = cmdWrapper.StatementHandle;
            int cColsAffected;

            // maps an enforced 4-part qualified string as follows
            // parts[0] = null  - ignored but removal would be a run-time breaking change from V1.0
            // parts[1] = CatalogName (optional, may be null)
            // parts[2] = SchemaName (optional, may be null)
            // parts[3] = ProcedureName
            //
            string quote = connection.QuoteChar(ADP.DeriveParameters);
            string[] parts = MultipartIdentifier.ParseMultipartIdentifier(command.CommandText, quote, quote, '.', 4, true, Res.ODBC_ODBCCommandText, false);          
            if (null == parts[3]) { // match everett behavior, if the commandtext is nothing but whitespace set the command text to the whitespace
                parts[3] = command.CommandText;
            }
            // note: native odbc appears to ignore all but the procedure name
            ODBC32.RetCode retcode = hstmt.ProcedureColumns(parts[1], parts[2], parts[3], null);

            // Note: the driver does not return an error if the given stored procedure does not exist
            // therefore we cannot handle that case and just return not parameters.

            if (ODBC32.RetCode.SUCCESS != retcode) {
                connection.HandleError(hstmt, retcode);
            }

            using (OdbcDataReader reader = new OdbcDataReader(command, cmdWrapper, CommandBehavior.Default)) {
                reader.FirstResult();
                cColsAffected = reader.FieldCount;

                // go through the returned rows and filter out relevant parameter data
                //
                while (reader.Read()) {
                    // devnote: column types are specified in the ODBC Programmer's Reference
                    // COLUMN_TYPE      Smallint    16bit
                    // COLUMN_SIZE      Integer     32bit
                    // DECIMAL_DIGITS   Smallint    16bit
                    // NUM_PREC_RADIX   Smallint    16bit

                    OdbcParameter parameter = new OdbcParameter();

                    parameter.ParameterName = reader.GetString(ODBC32.COLUMN_NAME-1);
                    switch ((ODBC32.SQL_PARAM)reader.GetInt16(ODBC32.COLUMN_TYPE-1)){
                        case ODBC32.SQL_PARAM.INPUT:
                            parameter.Direction = ParameterDirection.Input;
                            break;
                        case ODBC32.SQL_PARAM.OUTPUT:
                            parameter.Direction = ParameterDirection.Output;
                            break;

                        case ODBC32.SQL_PARAM.INPUT_OUTPUT:
                            parameter.Direction = ParameterDirection.InputOutput;
                            break;
                        case ODBC32.SQL_PARAM.RETURN_VALUE:
                            parameter.Direction = ParameterDirection.ReturnValue;
                            break;
                        default:
                            Debug.Assert(false, "Unexpected Parametertype while DeriveParamters");
                            break;
                    }
                    parameter.OdbcType = TypeMap.FromSqlType((ODBC32.SQL_TYPE)reader.GetInt16(ODBC32.DATA_TYPE-1))._odbcType;
                    parameter.Size = (int)reader.GetInt32(ODBC32.COLUMN_SIZE-1);
                    switch(parameter.OdbcType){
                        case OdbcType.Decimal:
                        case OdbcType.Numeric:
                            parameter.ScaleInternal = (Byte)reader.GetInt16(ODBC32.DECIMAL_DIGITS-1);
                            parameter.PrecisionInternal = (Byte)reader.GetInt16(ODBC32.NUM_PREC_RADIX-1);
                        break;
                    }
                    rParams.Add (parameter);
                }
            }
            retcode = hstmt.CloseCursor();
            return rParams.ToArray();;
        }

        public override string QuoteIdentifier(string unquotedIdentifier){
            return QuoteIdentifier(unquotedIdentifier, null /* use DataAdapter.SelectCommand.Connection if available */);
        }
        public string QuoteIdentifier( string unquotedIdentifier, OdbcConnection connection){

            ADP.CheckArgumentNull(unquotedIdentifier, "unquotedIdentifier");

            // if the user has specificed a prefix use the user specified  prefix and suffix
            // otherwise get them from the provider
            string quotePrefix = QuotePrefix;
            string quoteSuffix = QuoteSuffix;
            if (ADP.IsEmpty(quotePrefix) == true) {
                if (connection == null) {
                    // VSTFDEVDIV 479567: use the adapter's connection if QuoteIdentifier was called from 
                    // DbCommandBuilder instance (which does not have an overload that gets connection object)
                    connection = base.GetConnection() as OdbcConnection;
                    if (connection == null) {
                        throw ADP.QuotePrefixNotSet(ADP.QuoteIdentifier);
                    }
                }
                quotePrefix = connection.QuoteChar(ADP.QuoteIdentifier);
                quoteSuffix = quotePrefix;
            }

            // by the ODBC spec "If the data source does not support quoted identifiers, a blank is returned."
            // So if a blank is returned the string is returned unchanged. Otherwise the returned string is used
            // to quote the string
            if ((ADP.IsEmpty(quotePrefix) == false) && (quotePrefix != " ")) {
                return ADP.BuildQuotedString(quotePrefix,quoteSuffix,unquotedIdentifier);
            }
            else {
                return unquotedIdentifier;
            }
        }



        override protected void SetRowUpdatingHandler(DbDataAdapter adapter) {
            Debug.Assert(adapter is OdbcDataAdapter, "!OdbcDataAdapter");
            if (adapter == base.DataAdapter) { // removal case
                ((OdbcDataAdapter)adapter).RowUpdating -= OdbcRowUpdatingHandler;
            }
            else { // adding case
                ((OdbcDataAdapter)adapter).RowUpdating += OdbcRowUpdatingHandler;
            }
        }

        public override string UnquoteIdentifier( string quotedIdentifier){
            return UnquoteIdentifier(quotedIdentifier, null /* use DataAdapter.SelectCommand.Connection if available */);
        }

        public string UnquoteIdentifier(string quotedIdentifier, OdbcConnection connection){

            ADP.CheckArgumentNull(quotedIdentifier, "quotedIdentifier");


            // if the user has specificed a prefix use the user specified  prefix and suffix
            // otherwise get them from the provider
            string quotePrefix = QuotePrefix;
            string quoteSuffix = QuoteSuffix;
            if (ADP.IsEmpty(quotePrefix) == true) {
                if (connection == null) {
                    // VSTFDEVDIV 479567: use the adapter's connection if UnquoteIdentifier was called from 
                    // DbCommandBuilder instance (which does not have an overload that gets connection object)
                    connection = base.GetConnection() as OdbcConnection;
                    if (connection == null) {
                        throw ADP.QuotePrefixNotSet(ADP.UnquoteIdentifier);
                    }
                }
                quotePrefix = connection.QuoteChar(ADP.UnquoteIdentifier);
                quoteSuffix = quotePrefix;
            }

            String unquotedIdentifier;
            // by the ODBC spec "If the data source does not support quoted identifiers, a blank is returned."
            // So if a blank is returned the string is returned unchanged. Otherwise the returned string is used
            // to unquote the string
            if ((ADP.IsEmpty(quotePrefix) == false) || (quotePrefix != " ")) {
                // ignoring the return value because it is acceptable for the quotedString to not be quoted in this
                // context.
                ADP.RemoveStringQuotes(quotePrefix, quoteSuffix, quotedIdentifier, out unquotedIdentifier);
            }
            else {
                unquotedIdentifier = quotedIdentifier;
            }
            return unquotedIdentifier;

        }

    }
}
