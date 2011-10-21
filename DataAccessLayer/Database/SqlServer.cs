using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Hosting;
using NewLife;

namespace XCode.DataAccessLayer
{
    class SqlServer : RemoteDb
    {
        #region 属性
        /// <summary>
        /// 返回数据库类型。外部DAL数据库类请使用Other
        /// </summary>
        public override DatabaseType DbType { get { return DatabaseType.SqlServer; } }

        /// <summary>工厂</summary>
        public override DbProviderFactory Factory { get { return SqlClientFactory.Instance; } }

        private Boolean? _IsSQL2005;
        /// <summary>是否SQL2005及以上</summary>
        public Boolean IsSQL2005
        {
            get
            {
                if (_IsSQL2005 == null)
                {
                    if (String.IsNullOrEmpty(ConnectionString)) return false;
                    try
                    {
                        //切换到master库
                        DbSession session = CreateSession() as DbSession;
                        String dbname = session.DatabaseName;
                        //如果指定了数据库名，并且不是master，则切换到master
                        if (!String.IsNullOrEmpty(dbname) && !String.Equals(dbname, SystemDatabaseName, StringComparison.OrdinalIgnoreCase))
                        {
                            session.DatabaseName = SystemDatabaseName;
                        }

                        //取数据库版本
                        if (!session.Opened) session.Open();
                        String ver = session.Conn.ServerVersion;
                        session.AutoClose();

                        _IsSQL2005 = !ver.StartsWith("08");

                        if (!String.IsNullOrEmpty(dbname) && !String.Equals(dbname, SystemDatabaseName, StringComparison.OrdinalIgnoreCase))
                        {
                            session.DatabaseName = dbname;
                        }
                    }
                    catch { _IsSQL2005 = false; }
                }
                return _IsSQL2005.Value;
            }
            set { _IsSQL2005 = value; }
        }

        const String Application_Name = "Application Name";
        protected override void OnSetConnectionString(XDbConnectionStringBuilder builder)
        {
            base.OnSetConnectionString(builder);

            if (!builder.ContainsKey(Application_Name))
            {
                String name = Runtime.IsWeb ? HostingEnvironment.SiteName : AppDomain.CurrentDomain.FriendlyName;
                builder[Application_Name] = String.Format("XCode_{0}_{1}", name, ConnName);
            }
        }
        #endregion

        #region 方法
        /// <summary>
        /// 创建数据库会话
        /// </summary>
        /// <returns></returns>
        protected override IDbSession OnCreateSession() { return new SqlServerSession(); }

        /// <summary>
        /// 创建元数据对象
        /// </summary>
        /// <returns></returns>
        protected override IMetaData OnCreateMetaData() { return new SqlServerMetaData(); }

        public override bool Support(string providerName)
        {
            providerName = providerName.ToLower();
            if (providerName.Contains("system.data.sqlclient")) return true;
            if (providerName.Contains("sql2008")) return true;
            if (providerName.Contains("sql2005")) return true;
            if (providerName.Contains("sql2000")) return true;
            if (providerName.Contains("sqlclient")) return true;
            if (providerName.Contains("mssql")) return true;

            return false;
        }
        #endregion

        #region 分页
        /// <summary>
        /// 构造分页SQL
        /// </summary>
        /// <param name="sql">SQL语句</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <param name="keyColumn">唯一键。用于not in分页</param>
        /// <returns>分页SQL</returns>
        public override String PageSplit(String sql, Int32 startRowIndex, Int32 maximumRows, String keyColumn)
        {
            // 从第一行开始，不需要分页
            if (startRowIndex <= 0 && maximumRows < 1) return sql;

            // 指定了起始行，并且是SQL2005及以上版本，使用RowNumber算法
            if (startRowIndex > 0 && IsSQL2005) return PageSplitRowNumber(sql, startRowIndex, maximumRows, keyColumn);

            // 如果没有Order By，直接调用基类方法
            // 先用字符串判断，命中率高，这样可以提高处理效率
            if (!sql.Contains(" Order "))
            {
                if (!sql.ToLower().Contains(" order ")) return base.PageSplit(sql, startRowIndex, maximumRows, keyColumn);
            }
            //// 使用正则进行严格判断。必须包含Order By，并且它右边没有右括号)，表明有order by，且不是子查询的，才需要特殊处理
            //MatchCollection ms = Regex.Matches(sql, @"\border\s*by\b([^)]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            //if (ms == null || ms.Count < 1 || ms[0].Index < 1)
            String sql2 = sql;
            String orderBy = CheckOrderClause(ref sql2);
            if (String.IsNullOrEmpty(orderBy))
            {
                return base.PageSplit(sql, startRowIndex, maximumRows, keyColumn);
            }
            // 已确定该sql最外层含有order by，再检查最外层是否有top。因为没有top的order by是不允许作为子查询的
            if (Regex.IsMatch(sql, @"^[^(]+\btop\b", RegexOptions.Compiled | RegexOptions.IgnoreCase))
            {
                return base.PageSplit(sql, startRowIndex, maximumRows, keyColumn);
            }
            //String orderBy = sql.Substring(ms[0].Index);

            // 从第一行开始，不需要分页
            if (startRowIndex <= 0)
            {
                if (maximumRows < 1)
                    return sql;
                else
                    return String.Format("Select Top {0} * From {1} {2}", maximumRows, CheckSimpleSQL(sql2), orderBy);
                //return String.Format("Select Top {0} * From {1} {2}", maximumRows, CheckSimpleSQL(sql.Substring(0, ms[0].Index)), orderBy);
            }

            #region Max/Min分页
            // 如果要使用max/min分页法，首先keyColumn必须有asc或者desc
            String kc = keyColumn.ToLower();
            if (kc.EndsWith(" desc") || kc.EndsWith(" asc") || kc.EndsWith(" unknown"))
            {
                String str = PageSplitMaxMin(sql, startRowIndex, maximumRows, keyColumn);
                if (!String.IsNullOrEmpty(str)) return str;
                keyColumn = keyColumn.Substring(0, keyColumn.IndexOf(" "));
            }
            #endregion

            sql = CheckSimpleSQL(sql2);

            if (String.IsNullOrEmpty(keyColumn)) throw new ArgumentNullException("keyColumn", "分页要求指定主键列或者排序字段！");

            if (maximumRows < 1)
                sql = String.Format("Select * From {1} Where {2} Not In(Select Top {0} {2} From {1} {3}) {3}", startRowIndex, sql, keyColumn, orderBy);
            else
                sql = String.Format("Select Top {0} * From {1} Where {2} Not In(Select Top {3} {2} From {1} {4}) {4}", maximumRows, sql, keyColumn, startRowIndex, orderBy);
            return sql;
        }

        /// <summary>
        /// 已重写。获取分页
        /// </summary>
        /// <param name="sql">SQL语句</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <param name="keyColumn">主键列。用于not in分页</param>
        /// <returns></returns>
        public String PageSplitRowNumber(String sql, Int32 startRowIndex, Int32 maximumRows, String keyColumn)
        {
            // 从第一行开始，不需要分页
            if (startRowIndex <= 0)
            {
                if (maximumRows < 1)
                    return sql;
                else
                    return base.PageSplit(sql, startRowIndex, maximumRows, keyColumn);
            }

            String orderBy = String.Empty;
            if (sql.ToLower().Contains(" order "))
            {
                // 使用正则进行严格判断。必须包含Order By，并且它右边没有右括号)，表明有order by，且不是子查询的，才需要特殊处理
                //MatchCollection ms = Regex.Matches(sql, @"\border\s*by\b([^)]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                //if (ms != null && ms.Count > 0 && ms[0].Index > 0)
                String sql2 = sql;
                String orderBy2 = CheckOrderClause(ref sql2);
                if (String.IsNullOrEmpty(orderBy))
                {
                    // 已确定该sql最外层含有order by，再检查最外层是否有top。因为没有top的order by是不允许作为子查询的
                    if (!Regex.IsMatch(sql, @"^[^(]+\btop\b", RegexOptions.Compiled | RegexOptions.IgnoreCase))
                    {
                        //orderBy = sql.Substring(ms[0].Index).Trim();
                        //sql = sql.Substring(0, ms[0].Index).Trim();
                        orderBy = orderBy2.Trim();
                        sql = sql2.Trim();
                    }
                }
            }

            if (String.IsNullOrEmpty(orderBy))
            {
                if (String.IsNullOrEmpty(keyColumn)) throw new ArgumentNullException("keyColumn", "分页要求指定主键列或者排序字段！");

                //if (keyColumn.EndsWith(" Unknown", StringComparison.OrdinalIgnoreCase)) keyColumn = keyColumn.Substring(0, keyColumn.LastIndexOf(" "));
                orderBy = "Order By " + keyColumn;
            }
            sql = CheckSimpleSQL(sql);

            //row_number()从1开始
            if (maximumRows < 1)
                sql = String.Format("Select * From (Select *, row_number() over({2}) as rowNumber From {1}) XCode_Temp_b Where rowNumber>={0}", startRowIndex + 1, sql, orderBy);
            else
                sql = String.Format("Select * From (Select *, row_number() over({3}) as rowNumber From {1}) XCode_Temp_b Where rowNumber Between {0} And {2}", startRowIndex + 1, sql, startRowIndex + maximumRows, orderBy);

            return sql;
        }

        public override string PageSplit(SelectBuilder builder, int startRowIndex, int maximumRows, string keyColumn)
        {
            // 从第一行开始，不需要分页
            if (startRowIndex <= 0 && maximumRows < 1) return builder.ToString();
            if (startRowIndex <= 0 && maximumRows > 0) return PageSplitTop(builder, maximumRows, null).ToString();

            if (String.IsNullOrEmpty(builder.GroupBy))
            {
                // 指定了起始行，并且是SQL2005及以上版本，使用RowNumber算法
                if (startRowIndex > 0 && IsSQL2005) return PageSplitRowNumber(builder.ToString(), startRowIndex, maximumRows, keyColumn);

                return PageSplitTopNotIn(builder, startRowIndex, maximumRows, keyColumn);
            }

            return base.PageSplit(builder, startRowIndex, maximumRows, keyColumn);
        }
        #endregion

        #region MS分页
        /// <summary>
        /// 构造分页SQL，优先选择max/min，然后选择not in
        /// </summary>
        /// <param name="builder">查询生成器</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <param name="keyColumn">唯一键。用于not in分页</param>
        /// <returns>分页SQL</returns>
        internal static String PageSplitTopNotIn(SelectBuilder builder, Int32 startRowIndex, Int32 maximumRows, String keyColumn)
        {
            //TODO 这里有极大的风险，比如分组就无法支持

            // 从第一行开始，不需要分页
            if (startRowIndex <= 0 && maximumRows < 1) return builder.ToString();
            if (startRowIndex <= 0 && maximumRows > 0) return PageSplitTop(builder, maximumRows, null).ToString();

            #region Max/Min分页
            // 如果要使用max/min分页法，首先keyColumn必须有asc或者desc
            if (!String.IsNullOrEmpty(keyColumn))
            {
                String kc = keyColumn.ToLower();
                if (kc.EndsWith(" desc") || kc.EndsWith(" asc") || kc.EndsWith(" unknown"))
                {
                    String str = PageSplitMaxMin(builder, startRowIndex, maximumRows, keyColumn);
                    if (!String.IsNullOrEmpty(str)) return str;

                    // 如果不能使用最大最小值分页，则砍掉排序，为TopNotIn分页做准备
                    keyColumn = keyColumn.Substring(0, keyColumn.IndexOf(" "));
                }
            }
            #endregion

            //if (startRowIndex <= 0 && maximumRows > 0) return PageSplitTop(builder, maximumRows, null).ToString();

            if (String.IsNullOrEmpty(keyColumn)) throw new ArgumentNullException("keyColumn", "这里用的not in分页算法要求指定主键列！");

            //if (maximumRows < 1)
            //{
            //    sql = String.Format("Select * From {1} Where {2} Not In(Select Top {0} {2} From {1})", startRowIndex, sql, keyColumn);
            //}
            //else
            //{
            //    sql = String.Format("Select Top {0} * From {1} Where {2} Not In(Select Top {3} {2} From {1})", maximumRows, sql, keyColumn, startRowIndex);
            //}

            SelectBuilder builder2 = PageSplitTop(builder, startRowIndex, keyColumn);

            // maximumRows > 0时加个Top，别的一样
            if (maximumRows > 0) builder = PageSplitTop(builder, maximumRows, null);
            // 如果本来有Where字句，加上And，当然，要区分情况按是否有必要加圆括号
            if (!String.IsNullOrEmpty(builder.Where))
            {
                if (builder.Where.Contains(" ") && builder.Where.ToLower().Contains("or"))
                    builder.Where = String.Format("({0}) And ", builder.Where);
                else
                    builder.Where = builder.Where + " And ";
            }
            builder.Where += String.Format("{0} Not In({1})", keyColumn, builder2.ToString());

            return builder.ToString();
        }

        /// <summary>
        /// 按唯一数字最大最小分析
        /// </summary>
        /// <param name="builder">查询生成器</param>
        /// <param name="startRowIndex">开始行，0表示第一行</param>
        /// <param name="maximumRows">最大返回行数，0表示所有行</param>
        /// <param name="keyColumn">唯一键。用于not in分页</param>
        /// <returns>分页SQL</returns>
        protected static String PageSplitMaxMin(SelectBuilder builder, Int32 startRowIndex, Int32 maximumRows, String keyColumn)
        {
            if (startRowIndex <= 0 && maximumRows > 0) return PageSplitTop(builder, maximumRows, null).ToString();

            // 唯一键的顺序。默认为Empty，可以为asc或desc，如果有，则表明主键列是数字唯一列，可以使用max/min分页法
            Boolean isAscOrder = keyColumn.ToLower().EndsWith(" asc");
            // 是否使用max/min分页法
            Boolean canMaxMin = false;

            // 如果sql最外层有排序，且唯一的一个排序字段就是keyColumn时，可用max/min分页法
            // 如果sql最外层没有排序，其排序不是unknown，可用max/min分页法
            if (!String.IsNullOrEmpty(builder.OrderBy))
            {
                #region 有OrderBy
                keyColumn = keyColumn.Substring(0, keyColumn.IndexOf(" "));

                String strOrderBy = builder.OrderBy;
                //builder = builder.Clone();
                //builder.OrderBy = null;

                // 只有一个排序字段
                if (!String.IsNullOrEmpty(strOrderBy) && !strOrderBy.Contains(","))
                {
                    // 有asc或者desc。没有时，默认为asc
                    if (strOrderBy.ToLower().EndsWith(" desc"))
                    {
                        String str = strOrderBy.Substring(0, strOrderBy.Length - " desc".Length).Trim();
                        // 排序字段等于keyColumn
                        if (str.ToLower() == keyColumn.ToLower())
                        {
                            isAscOrder = false;
                            canMaxMin = true;
                        }
                    }
                    else if (strOrderBy.ToLower().EndsWith(" asc"))
                    {
                        String str = strOrderBy.Substring(0, strOrderBy.Length - " asc".Length).Trim();
                        // 排序字段等于keyColumn
                        if (str.ToLower() == keyColumn.ToLower())
                        {
                            isAscOrder = true;
                            canMaxMin = true;
                        }
                    }
                    else if (!strOrderBy.Contains(" ")) // 不含空格，是唯一排序字段
                    {
                        // 排序字段等于keyColumn
                        if (strOrderBy.ToLower() == keyColumn.ToLower())
                        {
                            isAscOrder = true;
                            canMaxMin = true;
                        }
                    }
                }
                #endregion
            }
            else
            {
                // 取第一页也不用分页。把这代码放到这里，主要是数字分页中要自己处理这种情况
                if (startRowIndex <= 0 && maximumRows > 0)
                {
                    ////数字分页中，业务上一般使用降序，Entity类会给keyColumn指定降序的
                    ////但是，在第一页的时候，没有用到keyColumn，而数据库一般默认是升序
                    ////这时候就会出现第一页是升序，后面页是降序的情况了。这里改正这个BUG
                    //if (keyColumn.ToLower().EndsWith(" desc") || keyColumn.ToLower().EndsWith(" asc"))
                    //    return String.Format("Select Top {0} * From {1} Order By {2}", maximumRows, CheckSimpleSQL(sql), keyColumn);
                    //else
                    //    return String.Format("Select Top {0} * From {1}", maximumRows, CheckSimpleSQL(sql));

                    builder = PageSplitTop(builder, maximumRows, null);

                }

                if (!keyColumn.ToLower().EndsWith(" unknown")) canMaxMin = true;

                keyColumn = keyColumn.Substring(0, keyColumn.IndexOf(" "));
            }

            if (!canMaxMin) return null;

            //if (maximumRows < 1)
            //    sql = String.Format("Select * From {1} Where {2}{3}(Select {4}({2}) From (Select Top {0} {2} From {1} Order By {2} {5}) XCode_Temp_a) Order By {2} {5}", startRowIndex, CheckSimpleSQL(sql), keyColumn, isAscOrder ? ">" : "<", isAscOrder ? "max" : "min", isAscOrder ? "Asc" : "Desc");
            //else
            //    sql = String.Format("Select Top {0} * From {1} Where {2}{4}(Select {5}({2}) From (Select Top {3} {2} From {1} Order By {2} {6}) XCode_Temp_a) Order By {2} {6}", maximumRows, CheckSimpleSQL(sql), keyColumn, startRowIndex, isAscOrder ? ">" : "<", isAscOrder ? "max" : "min", isAscOrder ? "Asc" : "Desc");
            //return sql;

            SelectBuilder builder2 = PageSplitTop(builder, startRowIndex, keyColumn);
            builder2.OrderBy = keyColumn + " " + (isAscOrder ? "Asc" : "Desc");
            String sql = String.Format("Select {0}({1}) From ({2}) XCode_Temp_a", isAscOrder ? "max" : "min", keyColumn, builder2.ToString());

            // maximumRows > 0时加个Top，别的一样
            if (maximumRows > 0) builder = PageSplitTop(builder, maximumRows, null);
            // 如果本来有Where字句，加上And，当然，要区分情况按是否有必要加圆括号
            if (!String.IsNullOrEmpty(builder.Where))
            {
                if (builder.Where.Contains(" ") && builder.Where.ToLower().Contains("or"))
                    builder.Where = String.Format("({0}) And ", builder.Where);
                else
                    builder.Where = builder.Where + " And ";
            }
            builder.Where += String.Format("{0}{2}({1})", keyColumn, sql, isAscOrder ? ">" : "<");

            return builder.ToString();
        }
        #endregion

        #region 数据库特性
        /// <summary>
        /// 当前时间函数
        /// </summary>
        public override String DateTimeNow { get { return "getdate()"; } }

        /// <summary>
        /// 最小时间
        /// </summary>
        public override DateTime DateTimeMin { get { return SqlDateTime.MinValue.Value; } }

        /// <summary>
        /// 长文本长度
        /// </summary>
        public override Int32 LongTextLength { get { return 4000; } }

        /// <summary>
        /// 格式化时间为SQL字符串
        /// </summary>
        /// <param name="dateTime">时间值</param>
        /// <returns></returns>
        public override String FormatDateTime(DateTime dateTime) { return "{ts" + String.Format("'{0:yyyy-MM-dd HH:mm:ss}'", dateTime) + "}"; }

        /// <summary>
        /// 格式化关键字
        /// </summary>
        /// <param name="keyWord">关键字</param>
        /// <returns></returns>
        public override String FormatKeyWord(String keyWord)
        {
            //if (String.IsNullOrEmpty(keyWord)) throw new ArgumentNullException("keyWord");
            if (String.IsNullOrEmpty(keyWord)) return keyWord;

            if (keyWord.StartsWith("[") && keyWord.EndsWith("]")) return keyWord;

            return String.Format("[{0}]", keyWord);
        }

        /// <summary>系统数据库名</summary>
        public override String SystemDatabaseName { get { return "master"; } }

        public override string FormatValue(IDataColumn field, object value)
        {
            TypeCode code = Type.GetTypeCode(field.DataType);
            Boolean isNullable = field.Nullable;

            if (code == TypeCode.String)
            {
                // 热心网友 Hannibal 在处理日文网站时发现插入的日文为乱码，这里加上N前缀
                if (value == null) return isNullable ? "null" : "''";
                if (String.IsNullOrEmpty(value.ToString()) && isNullable) return "null";

                // 这里直接判断原始数据类型有所不妥，如果原始数据库不是当前数据库，那么这里的判断将会失效
                // 一个可行的办法就是给XField增加一个IsUnicode属性，但如此一来，XField就稍微变大了
                // 目前暂时影响不大，后面看情况决定是否增加吧
                //if (field.RawType == "ntext" ||
                //    !String.IsNullOrEmpty(field.RawType) && (field.RawType.StartsWith("nchar") || field.RawType.StartsWith("nvarchar")))

                // 为了兼容旧版本实体类
                if (field.IsUnicode || IsUnicode(field.RawType))
                    return "N'" + value.ToString().Replace("'", "''") + "'";
                else
                    return "'" + value.ToString().Replace("'", "''") + "'";
            }
            //else if (field.DataType == typeof(Guid))
            //{
            //    if (value == null) return isNullable ? "null" : "''";

            //    return String.Format("'{0}'", value);
            //}

            return base.FormatValue(field, value);
        }
        #endregion
    }

    /// <summary>
    /// SqlServer数据库
    /// </summary>
    internal class SqlServerSession : RemoteDbSession
    {
        #region 查询
        /// <summary>
        /// 快速查询单表记录数，稍有偏差
        /// </summary>
        /// <param name="tableName"></param>
        /// <returns></returns>
        public override Int64 QueryCountFast(string tableName)
        {
            String sql = String.Format("select rows from sysindexes where id = object_id('{0}') and indid in (0,1)", tableName);
            return ExecuteScalar<Int64>(sql);

            //QueryTimes++;
            //DbCommand cmd = CreateCommand();
            //cmd.CommandText = sql;
            //WriteSQL(cmd.CommandText);
            //try
            //{
            //    return Convert.ToInt64(cmd.ExecuteScalar());
            //}
            //catch (DbException ex)
            //{
            //    throw OnException(ex, cmd.CommandText);
            //}
            //finally { AutoClose(); }
        }

        /// <summary>
        /// 执行插入语句并返回新增行的自动编号
        /// </summary>
        /// <param name="sql">SQL语句</param>
        /// <returns>新增行的自动编号</returns>
        public override Int64 InsertAndGetIdentity(string sql)
        {
            return ExecuteScalar<Int64>("SET NOCOUNT ON;" + sql + ";Select SCOPE_IDENTITY()");
        }
        #endregion
    }

    /// <summary>
    /// SqlServer元数据
    /// </summary>
    class SqlServerMetaData : RemoteDbMetaData
    {
        #region 属性
        /// <summary>
        /// 是否SQL2005
        /// </summary>
        public Boolean IsSQL2005 { get { return (Database as SqlServer).IsSQL2005; } }

        /// <summary>
        /// 0级类型
        /// </summary>
        public String level0type { get { return IsSQL2005 ? "SCHEMA" : "USER"; } }
        #endregion

        #region 构架
        /// <summary>
        /// 取得所有表构架
        /// </summary>
        /// <returns></returns>
        protected override List<IDataTable> OnGetTables()
        {
            #region 查表说明、字段信息、索引信息
            IDbSession session = Database.CreateSession();

            //一次性把所有的表说明查出来
            DataTable DescriptionTable = null;

            Boolean b = DbSession.ShowSQL;
            DbSession.ShowSQL = false;
            try
            {
                DescriptionTable = session.Query(DescriptionSql).Tables[0];
            }
            catch { }
            DbSession.ShowSQL = b;

            DataTable dt = GetSchema(_.Tables, null);
            if (dt == null || dt.Rows == null || dt.Rows.Count < 1) return null;

            b = DbSession.ShowSQL;
            DbSession.ShowSQL = false;
            try
            {
                AllFields = session.Query(SchemaSql).Tables[0];
                AllIndexes = session.Query(IndexSql).Tables[0];
            }
            catch { }
            DbSession.ShowSQL = b;
            #endregion

            // 列出用户表
            DataRow[] rows = dt.Select(String.Format("{0}='BASE TABLE' Or {0}='VIEW'", "TABLE_TYPE"));
            List<IDataTable> list = GetTables(rows);
            if (list == null || list.Count < 1) return list;

            // 修正备注
            foreach (IDataTable item in list)
            {
                DataRow[] drs = DescriptionTable == null ? null : DescriptionTable.Select("n='" + item.Name + "'");
                item.Description = drs == null || drs.Length < 1 ? "" : drs[0][1].ToString();
            }

            return list;
        }

        private DataTable AllFields = null;
        private DataTable AllIndexes = null;

        protected override void FixField(IDataColumn field, DataRow dr)
        {
            base.FixField(field, dr);

            DataRow[] rows = AllFields == null ? null : AllFields.Select("表名='" + field.Table.Name + "' And 字段名='" + field.Name + "'", null);
            if (rows != null && rows.Length > 0)
            {
                DataRow dr2 = rows[0];

                field.Identity = GetDataRowValue<Boolean>(dr2, "标识");
                field.PrimaryKey = GetDataRowValue<Boolean>(dr2, "主键");
                field.NumOfByte = GetDataRowValue<Int32>(dr2, "占用字节数");
                field.Description = GetDataRowValue<String>(dr2, "字段说明");
            }

            // 整理默认值
            if (!String.IsNullOrEmpty(field.Default))
            {
                field.Default = Trim(field.Default, "(", ")");
                field.Default = Trim(field.Default, "\"", "\"");
                field.Default = Trim(field.Default, "\'", "\'");
                field.Default = Trim(field.Default, "N\'", "\'");
                field.Default = field.Default.Replace("''", "'");
            }
        }

        protected override List<IDataIndex> GetIndexes(IDataTable table)
        {
            List<IDataIndex> list = base.GetIndexes(table);
            if (list != null && list.Count > 0)
            {
                foreach (IDataIndex item in list)
                {
                    DataRow[] drs = AllIndexes == null ? null : AllIndexes.Select("name='" + item.Name + "'");
                    if (drs != null && drs.Length > 0)
                    {
                        item.Unique = GetDataRowValue<Boolean>(drs[0], "is_unique");
                        item.PrimaryKey = GetDataRowValue<Boolean>(drs[0], "is_primary_key");
                    }
                }
            }
            return list;
        }

        public override string CreateTableSQL(IDataTable table)
        {
            String sql = base.CreateTableSQL(table);
            if (String.IsNullOrEmpty(sql) || table.PrimaryKeys == null || table.PrimaryKeys.Length < 2) return sql;

            // 处理多主键
            StringBuilder sb = new StringBuilder();
            foreach (IDataColumn item in table.PrimaryKeys)
            {
                if (sb.Length > 0) sb.Append(",");
                sb.Append(FormatName(item.Name));
            }
            sql += "; " + Environment.NewLine;
            sql += String.Format("Alter Table {0} Add Constraint PK_{1} Primary Key Clustered({2})", FormatName(table.Name), table.Name, sb.ToString());
            return sql;
        }

        public override string FieldClause(IDataColumn field, bool onlyDefine)
        {
            if (!String.IsNullOrEmpty(field.RawType) && field.RawType.Contains("char(-1)"))
            {
                if (IsSQL2005)
                    field.RawType = field.RawType.Replace("char(-1)", "char(MAX)");
                else
                    field.RawType = field.RawType.Replace("char(-1)", "char(" + (Int32.MaxValue / 2) + ")");
            }

            return base.FieldClause(field, onlyDefine);
        }

        protected override string GetFieldConstraints(IDataColumn field, Boolean onlyDefine)
        {
            // 非定义时（修改字段），主键字段没有约束
            if (!onlyDefine && field.PrimaryKey) return null;

            String str = base.GetFieldConstraints(field, onlyDefine);

            // 非定义时，自增字段没有约束
            if (onlyDefine && field.Identity) str = " IDENTITY(1,1)" + str;

            return str;
        }

        protected override string GetFormatParam(IDataColumn field, DataRow dr)
        {
            String str = base.GetFormatParam(field, dr);
            if (String.IsNullOrEmpty(str)) return str;

            // 这个主要来自于float，因为无法取得其精度
            if (str == "(0)") return null;
            return str;
        }

        protected override string GetFormatParamItem(IDataColumn field, DataRow dr, string item)
        {
            String pi = base.GetFormatParamItem(field, dr, item);
            if (field.DataType == typeof(String) && pi == "-1" && IsSQL2005) return "MAX";
            return pi;
        }
        #endregion

        #region 取得字段信息的SQL模版
        private String _SchemaSql = "";
        /// <summary>构架SQL</summary>
        public virtual String SchemaSql
        {
            get
            {
                if (String.IsNullOrEmpty(_SchemaSql))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("SELECT ");
                    sb.Append("表名=d.name,");
                    sb.Append("字段序号=a.colorder,");
                    sb.Append("字段名=a.name,");
                    sb.Append("标识=case when COLUMNPROPERTY( a.id,a.name,'IsIdentity')=1 then Convert(Bit,1) else Convert(Bit,0) end,");
                    sb.Append("主键=case when exists(SELECT 1 FROM sysobjects where xtype='PK' and name in (");
                    sb.Append("SELECT name FROM sysindexes WHERE id = a.id AND indid in(");
                    sb.Append("SELECT indid FROM sysindexkeys WHERE id = a.id AND colid=a.colid");
                    sb.Append("))) then Convert(Bit,1) else Convert(Bit,0) end,");
                    sb.Append("类型=b.name,");
                    sb.Append("占用字节数=a.length,");
                    sb.Append("长度=COLUMNPROPERTY(a.id,a.name,'PRECISION'),");
                    sb.Append("小数位数=isnull(COLUMNPROPERTY(a.id,a.name,'Scale'),0),");
                    sb.Append("允许空=case when a.isnullable=1 then Convert(Bit,1)else Convert(Bit,0) end,");
                    sb.Append("默认值=isnull(e.text,''),");
                    sb.Append("字段说明=isnull(g.[value],'')");
                    sb.Append("FROM syscolumns a ");
                    sb.Append("left join systypes b on a.xtype=b.xusertype ");
                    sb.Append("inner join sysobjects d on a.id=d.id  and d.xtype='U' ");
                    sb.Append("left join syscomments e on a.cdefault=e.id ");
                    if (IsSQL2005)
                    {
                        sb.Append("left join sys.extended_properties g on a.id=g.major_id and a.colid=g.minor_id and g.name = 'MS_Description'  ");
                    }
                    else
                    {
                        sb.Append("left join sysproperties g on a.id=g.id and a.colid=g.smallid  ");
                    }
                    sb.Append("order by a.id,a.colorder");
                    _SchemaSql = sb.ToString();
                }
                return _SchemaSql;
            }
        }

        private String _IndexSql;
        public virtual String IndexSql
        {
            get
            {
                if (_IndexSql == null)
                {
                    if (IsSQL2005)
                        _IndexSql = "select ind.* from sys.indexes ind inner join sys.objects obj on ind.object_id = obj.object_id where obj.type='u'";
                    else
                        _IndexSql = "select case ObjectProperty(object_id(ind.name),'IsUniqueCnst') as is_unique,case ObjectProperty( object_id(ind.name),'IsPrimaryKey') as is_primary_key,ind.* from sysindexes ind inner join sysobjects obj on ind.id = obj.id where obj.type='u'";
                }
                return _IndexSql;
            }
        }

        private readonly String _DescriptionSql2000 = "select b.name n, a.value v from sysproperties a inner join sysobjects b on a.id=b.id where a.smallid=0";
        private readonly String _DescriptionSql2005 = "select b.name n, a.value v from sys.extended_properties a inner join sysobjects b on a.major_id=b.id and a.minor_id=0 and a.name = 'MS_Description'";
        /// <summary>取表说明SQL</summary>
        public virtual String DescriptionSql { get { return IsSQL2005 ? _DescriptionSql2005 : _DescriptionSql2000; } }
        #endregion

        #region 数据定义
        public override object SetSchema(DDLSchema schema, params object[] values)
        {
            IDbSession session = Database.CreateSession();

            Object obj = null;
            String dbname = String.Empty;
            String databaseName = String.Empty;
            switch (schema)
            {
                case DDLSchema.DatabaseExist:
                    databaseName = values == null || values.Length < 1 ? null : (String)values[0];
                    if (String.IsNullOrEmpty(databaseName)) databaseName = session.DatabaseName;

                    dbname = session.DatabaseName;
                    session.DatabaseName = SystemDatabaseName;
                    obj = DatabaseExist(databaseName);

                    session.DatabaseName = dbname;
                    return obj;
                case DDLSchema.DropDatabase:
                    databaseName = values == null || values.Length < 1 ? null : (String)values[0];
                    if (String.IsNullOrEmpty(databaseName)) databaseName = session.DatabaseName;
                    values = new Object[] { databaseName, values == null || values.Length < 2 ? null : values[1] };

                    dbname = session.DatabaseName;
                    session.DatabaseName = SystemDatabaseName;
                    //obj = base.SetSchema(schema, values);
                    //if (Execute(String.Format("Drop Database [{0}]", dbname)) < 1)
                    //{
                    //    Execute(DropDatabaseSQL(databaseName));
                    //}
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("use master");
                    sb.AppendLine(";");
                    sb.AppendLine("declare   @spid   varchar(20),@dbname   varchar(20)");
                    sb.AppendLine("declare   #spid   cursor   for");
                    sb.AppendFormat("select   spid=cast(spid   as   varchar(20))   from   master..sysprocesses   where   dbid=db_id('{0}')", dbname);
                    sb.AppendLine();
                    sb.AppendLine("open   #spid");
                    sb.AppendLine("fetch   next   from   #spid   into   @spid");
                    sb.AppendLine("while   @@fetch_status=0");
                    sb.AppendLine("begin");
                    sb.AppendLine("exec('kill   '+@spid)");
                    sb.AppendLine("fetch   next   from   #spid   into   @spid");
                    sb.AppendLine("end");
                    sb.AppendLine("close   #spid");
                    sb.AppendLine("deallocate   #spid");

                    Int32 count = 0;
                    try { count = session.Execute(sb.ToString()); }
                    catch { }
                    obj = session.Execute(String.Format("Drop Database {0}", FormatName(dbname))) > 0;
                    //sb.AppendFormat("Drop Database [{0}]", dbname);

                    session.DatabaseName = dbname;
                    return obj;
                case DDLSchema.TableExist:
                    return TableExist((IDataTable)values[0]);
                default:
                    break;
            }
            return base.SetSchema(schema, values);
        }

        public override string CreateDatabaseSQL(string dbname, string file)
        {
            if (String.IsNullOrEmpty(file)) return String.Format("CREATE DATABASE {0}", FormatName(dbname));

            String logfile = String.Empty;

            if (!Path.IsPathRooted(file)) file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
            if (String.IsNullOrEmpty(Path.GetExtension(file))) file += ".mdf";
            logfile = Path.ChangeExtension(file, ".ldf");

            String dir = Path.GetDirectoryName(file);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            StringBuilder sb = new StringBuilder();

            sb.AppendFormat("CREATE DATABASE {0} ON  PRIMARY", FormatName(dbname));
            sb.AppendLine();
            sb.AppendFormat(@"( NAME = N'{0}', FILENAME = N'{1}', SIZE = 10 , MAXSIZE = UNLIMITED, FILEGROWTH = 10%)", dbname, file);
            sb.AppendLine();
            sb.Append("LOG ON ");
            sb.AppendLine();
            sb.AppendFormat(@"( NAME = N'{0}_Log', FILENAME = N'{1}', SIZE = 10 , MAXSIZE = UNLIMITED, FILEGROWTH = 10%)", dbname, logfile);
            sb.AppendLine();

            return sb.ToString();
        }

        public override string DatabaseExistSQL(string dbname)
        {
            return String.Format("SELECT * FROM sysdatabases WHERE name = N'{0}'", dbname);
        }

        /// <summary>
        /// 使用数据架构确定数据库是否存在，因为使用系统视图可能没有权限
        /// </summary>
        /// <param name="dbname"></param>
        /// <returns></returns>
        public Boolean DatabaseExist(string dbname)
        {
            DataTable dt = GetSchema(_.Databases, new String[] { dbname });
            return dt != null && dt.Rows != null && dt.Rows.Count > 0;
        }

        public override string TableExistSQL(String tableName)
        {
            if (IsSQL2005)
                return String.Format("select * from sysobjects where xtype='U' and name='{0}'", tableName);
            else
                return String.Format("SELECT * FROM sysobjects WHERE id = OBJECT_ID(N'[dbo].{0}') AND OBJECTPROPERTY(id, N'IsUserTable') = 1", FormatName(tableName));
        }

        /// <summary>
        /// 使用数据架构确定数据表是否存在，因为使用系统视图可能没有权限
        /// </summary>
        /// <param name="table"></param>
        /// <returns></returns>
        public Boolean TableExist(IDataTable table)
        {
            DataTable dt = GetSchema(_.Tables, new String[] { null, null, table.Name, null });
            return dt != null && dt.Rows != null && dt.Rows.Count > 0;
        }

        public override string AddTableDescriptionSQL(IDataTable table)
        {
            return String.Format("EXEC dbo.sp_addextendedproperty @name=N'MS_Description', @value=N'{1}' , @level0type=N'{2}',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'{0}'", table.Name, table.Description, level0type);
        }

        public override string DropTableDescriptionSQL(IDataTable table)
        {
            return String.Format("EXEC dbo.sp_dropextendedproperty @name=N'MS_Description', @level0type=N'{1}',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'{0}'", table.Name, level0type);
        }

        public override string AddColumnSQL(IDataColumn field)
        {
            return String.Format("Alter Table {0} Add {1}", FormatName(field.Table.Name), FieldClause(field, true));
        }

        public override string AlterColumnSQL(IDataColumn field, IDataColumn oldfield)
        {
            // 创建为自增，先删后加
            if (field.Identity && !oldfield.Identity)
            {
                return DropColumnSQL(oldfield) + ";" + Environment.NewLine + AddColumnSQL(field);
            }

            String sql = String.Format("Alter Table {0} Alter Column {1}", FormatName(field.Table.Name), FieldClause(field, false));
            String pk = DeletePrimaryKeySQL(field);
            if (field.PrimaryKey)
            {
                // 如果没有主键删除脚本，表明没有主键
                //if (String.IsNullOrEmpty(pk))
                if (!oldfield.PrimaryKey)
                {
                    // 增加主键约束
                    pk = String.Format("Alter Table {0} ADD CONSTRAINT PK_{0} PRIMARY KEY {2}({1}) ON [PRIMARY]", FormatName(field.Table.Name), FormatName(field.Name), field.Identity ? "CLUSTERED" : "");
                    sql += ";" + Environment.NewLine + pk;
                }
            }
            else
            {
                // 字段声明没有主键，但是主键实际存在，则删除主键
                //if (!String.IsNullOrEmpty(pk))
                if (oldfield.PrimaryKey)
                {
                    sql += ";" + Environment.NewLine + pk;
                }
            }
            return sql;
        }

        public override string DropColumnSQL(IDataColumn field)
        {
            //删除默认值
            String sql = DropDefaultSQL(field);
            if (!String.IsNullOrEmpty(sql)) sql += ";" + Environment.NewLine;

            //删除主键
            String sql2 = DeletePrimaryKeySQL(field);
            if (!String.IsNullOrEmpty(sql2)) sql += sql2 + ";" + Environment.NewLine;

            sql += base.DropColumnSQL(field);
            return sql;
        }

        public override string AddColumnDescriptionSQL(IDataColumn field)
        {
            String sql = String.Format("EXEC dbo.sp_addextendedproperty @name=N'MS_Description', @value=N'{1}' , @level0type=N'{3}',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'{0}', @level2type=N'COLUMN',@level2name=N'{2}'", field.Table.Name, field.Description, field.Name, level0type);
            return sql;
        }

        public override string DropColumnDescriptionSQL(IDataColumn field)
        {
            return String.Format("EXEC dbo.sp_dropextendedproperty @name=N'MS_Description', @level0type=N'{2}',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'{0}', @level2type=N'COLUMN',@level2name=N'{1}'", field.Table.Name, field.Name, level0type);
        }

        public override string AddDefaultSQL(IDataColumn field)
        {
            String sql = DropDefaultSQL(field);
            if (!String.IsNullOrEmpty(sql)) sql += ";" + Environment.NewLine;
            if (Type.GetTypeCode(field.DataType) == TypeCode.String)
                sql += String.Format("Alter Table {0} Add CONSTRAINT DF_{0}_{1} DEFAULT N'{2}' FOR {1}", field.Table.Name, field.Name, field.Default);
            else if (Type.GetTypeCode(field.DataType) == TypeCode.DateTime)
            {
                String dv = CheckAndGetDefaultDateTimeNow(field.Table.DbType, field.Default);
                sql += String.Format("Alter Table {0} Add CONSTRAINT DF_{0}_{1} DEFAULT {2} FOR {1}", field.Table.Name, field.Name, dv);
            }
            else
                sql += String.Format("Alter Table {0} Add CONSTRAINT DF_{0}_{1} DEFAULT {2} FOR {1}", field.Table.Name, field.Name, field.Default);
            return sql;
        }

        public override string DropDefaultSQL(IDataColumn field)
        {
            if (String.IsNullOrEmpty(field.Default)) return String.Empty;

            String sql = null;
            if (IsSQL2005)
                sql = String.Format("select b.name from sys.tables a inner join sys.default_constraints b on a.object_id=b.parent_object_id inner join sys.columns c on a.object_id=c.object_id and b.parent_column_id=c.column_id where a.name='{0}' and c.name='{1}'", field.Table.Name, field.Name);
            else
                sql = String.Format("select b.name from syscolumns a inner join sysobjects b on a.cdefault=b.id inner join sysobjects c on a.id=c.id where a.name='{1}' and c.name='{0}' and b.xtype='D'", field.Table.Name, field.Name);

            DataSet ds = Database.CreateSession().Query(sql);
            if (ds == null || ds.Tables == null || ds.Tables[0].Rows.Count < 1) return null;

            StringBuilder sb = new StringBuilder();
            foreach (DataRow dr in ds.Tables[0].Rows)
            {
                String name = dr[0].ToString();
                if (sb.Length > 0) sb.AppendLine(";");
                sb.AppendFormat("Alter Table {0} Drop CONSTRAINT {1}", FormatName(field.Table.Name), name);
            }
            return sb.ToString();
        }

        String DeletePrimaryKeySQL(IDataColumn field)
        {
            if (!field.PrimaryKey) return String.Empty;

            if (field.Table.Indexes == null || field.Table.Indexes.Count < 1) return String.Empty;

            IDataIndex di = null;
            foreach (IDataIndex item in field.Table.Indexes)
            {
                if (Array.IndexOf(item.Columns, field.Name) >= 0)
                {
                    di = item;
                    break;
                }
            }
            if (di == null) return String.Empty;

            return String.Format("Alter Table {0} Drop CONSTRAINT {1}", FormatName(field.Table.Name), di.Name);
        }

        public override String DropDatabaseSQL(String dbname)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("use master");
            sb.AppendLine(";");
            sb.AppendLine("declare   @spid   varchar(20),@dbname   varchar(20)");
            sb.AppendLine("declare   #spid   cursor   for");
            sb.AppendFormat("select   spid=cast(spid   as   varchar(20))   from   master..sysprocesses   where   dbid=db_id('{0}')", dbname);
            sb.AppendLine();
            sb.AppendLine("open   #spid");
            sb.AppendLine("fetch   next   from   #spid   into   @spid");
            sb.AppendLine("while   @@fetch_status=0");
            sb.AppendLine("begin");
            sb.AppendLine("exec('kill   '+@spid)");
            sb.AppendLine("fetch   next   from   #spid   into   @spid");
            sb.AppendLine("end");
            sb.AppendLine("close   #spid");
            sb.AppendLine("deallocate   #spid");
            sb.AppendLine(";");
            sb.AppendFormat("Drop Database {0}", FormatName(dbname));
            return sb.ToString();
        }
        #endregion

        #region 辅助函数
        /// <summary>
        /// 除去字符串两端成对出现的符号
        /// </summary>
        /// <param name="str"></param>
        /// <param name="prefix"></param>
        /// <param name="suffix"></param>
        /// <returns></returns>
        public static String Trim(String str, String prefix, String suffix)
        {
            while (!String.IsNullOrEmpty(str))
            {
                if (!str.StartsWith(prefix)) return str;
                if (!str.EndsWith(suffix)) return str;

                str = str.Substring(prefix.Length, str.Length - suffix.Length - prefix.Length);
            }
            return str;
        }
        #endregion
    }
}