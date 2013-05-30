﻿using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Runtime.InteropServices;


//using System.Appdomain;


namespace TightDbCSharp
{
    using System.IO;
    using System.Globalization;

    //mirrors the enum in the C interface
    /*     
   Note: These must be kept in sync with those in
   <tightdb/data_type.hpp> of the core library. 
enum DataType {
    type_Int    =  0,
    type_Bool   =  1,
    type_Date   =  7,
    type_Float  =  9,
    type_Double = 10,
    type_String =  2,
    type_Binary =  4,
    type_Table  =  5,
    type_Mixed  =  6
};
     */

    public enum DataType
    {
        Int        =  0,
        Bool       =  1,
        String     =  2,
        Binary     =  4,
        Table      =  5,
        Mixed      =  6,
        Date       =  7,
        Float      =  9,
       Double      = 10
    }


    
    
    

    //this class contains methods for calling the c++ TightDB system, which has been flattened out as C type calls   
    //The individual public methods call the C inteface using types and values suitable for the C interface, but the methods take and give
    //values that are suitable for C# (for instance taking a C# Table object parameter and calling on with the C++ Table pointer inside the C# Table Class)
    //it is assumed that the deployment has copied the correct c++ dll into the same dir as where the calling assembly resides.
    //If this is not the case, a "badimage" exception runtime error will happen
    //we might want to catch that exception, then copy the correct dll into place, and then see if we can resume operation
    //alternatively even proactively copying the dll at startup if we detect that the wrong one is there
    //could be as simple as comparing file sizes or the like
    //because we expect the end user to have deployed the correct c++ dll this assembly is AnyCpu
    internal static class UnsafeNativeMethods
    {
        //This could become a global const as it should only be set once
        //we are a dll and as such do not have any code that is called on initialization (there is no initialization in a .net assembly)
        //however we could call a common init method from the few methods that the user can decide to start out with. Such as new table, new group etc.
        //the call would be called many times but could just return at once, if we were already initialized
        //and if we were not, we could set the is64bit boolean correctly - and then all other calls (does not call init) could just check that boolean
        //would speed up interop call overhead about 3-4 percent, currently at about 100 million a second on a 2.6GHZ cpu
        private static bool Is64Bit
        {
            get
            {
                return (UIntPtr.Size == 8);
                    //if this is evaluated every time, a faster way could be implemented. Size is cost when we are running though so perhaps it gets inlined by the JITter
            }
        }

        private static StringBuilder _callog; //used to collect call data when debugging small unit tests

        private static bool _loggingEnabled;

        private static bool LoggingEnabled
        {
            get { return _loggingEnabled; }
            set
            {
                if (value)
                {
                    if (_callog == null)
                    {
                        _callog = new StringBuilder();
                    }
                    _callog.AppendLine("--------LOGGING HAS BEEN ENABLED----------");
                    _loggingEnabled = true;
                }
                else
                {
                    _callog.AppendLine("--------LOGGING HAS BEEN DISABLED----------");
                    _loggingEnabled = false;
                }
            }
        }

        public static void LoggingSaveFile(String fileName)
        {
            if (LoggingEnabled)
            {
                File.WriteAllText(fileName, _callog.ToString());
            }
        }

        public static void LoggingDisable()
        {
            LoggingEnabled = false;
        }

        public static void LoggingEnable(string marker)
        {
            LoggingEnabled = true;
            if (!String.IsNullOrEmpty(marker))
            {
                _callog.AppendLine(String.Format(CultureInfo.InvariantCulture, "LOGGING ENABLED BY:{0}", marker));
            }
        }

        //idea:consider using some standard log framework, or remove logging altogether (logging was used in early debugging)
        public static void Log(string where, string desc, params object[] values)
        {
            if (LoggingEnabled)
            {
                _callog.Append(String.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss} {1} {2}",
                                             DateTime.UtcNow, where, desc));
                _callog.AppendLine(":");
                foreach (object o in values)
                {
                    string typestr = o.GetType().ToString();
                    string valuestr = o.ToString();
                    //if something doesn't auto.generate into readable code, we can test on o, and create custom more readable values
                    Type oType = o.GetType();
                    if (oType == typeof (Handled))
                    {
                        var handled = o as Handled;
                        if (handled != null) valuestr = handled.ObjectIdentification();
                    }

                    _callog.Append("Type(");
                    _callog.Append(typestr);
                    _callog.Append(") Value(");
                    _callog.Append(valuestr);
                    _callog.AppendLine(")");
                }
            }
        }

        //tightdb_c_cs_API size_t tightdb_c_csGetVersion(void)
        [DllImport("tightdb_c_cs64", EntryPoint = "tightdb_c_cs_getver", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr tightdb_c_cs_DllVer64();

        [DllImport("tightdb_c_cs32", EntryPoint = "tightdb_c_cs_getver", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr tightdb_c_cs_DllVer32();

        public static long CppDllVersion()
        {
            if (Is64Bit)
                return (long) tightdb_c_cs_DllVer64();
            return (long) tightdb_c_cs_DllVer32();
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "spec_deallocate", CallingConvention = CallingConvention.Cdecl)]
        private static extern void spec_deallocate64(IntPtr spec);

        [DllImport("tightdb_c_cs32", EntryPoint = "spec_deallocate", CallingConvention = CallingConvention.Cdecl)]
        private static extern void spec_deallocate32(IntPtr spec);

        public static void SpecDeallocate(Spec s)
        {
            if (Is64Bit)
                spec_deallocate64(s.Handle);
            else
                spec_deallocate32(s.Handle);
            s.Handle = IntPtr.Zero;
        }


        //we have to trust that c++ DataType fills up the same amount of stack space as one of our own DataType enum's
        //This is the case on windows, visual studio2010 and 2012 but Who knows if some c++ compiler somewhere someday decides to store DataType differently
        //Marshalling DataType seemed to work very well, but I have chosen to use size_t instead to be 100% sure stack sizes always match. after all, marshalling targets VS2012 on windows specifically
        //furthermore, the cast from IntPtr has been put in a method of its own to ease any fixes to this later on

        private static DataType IntPtrToDataType(IntPtr value)
        {
            return (DataType)value;
        }

        private static IntPtr DataTypeToIntPtr(DataType value)
        {
            return (IntPtr)value;
        }



        private static IntPtr BoolToIntPtr(Boolean value)
        {
            return value ? (IntPtr)1 : (IntPtr)0;
        }

        private static Boolean IntPtrToBool(IntPtr value)
        {
            return (IntPtr)1 == value;
        }

        // tightdb_c_cs_API size_t add_column(size_t SpecPtr,DataType type, const char* name) 

        //marshalling : not sure the simple enum members have the same size on C# and c++ on all platforms and bit sizes
        //and not sure if the marshaller will fix it for us if they are not of the same size
        //so this must be tested on various platforms and bit sizes, and perhaps specific versions of calls with enums have to be made
        //this one works on windows 7, .net 4.5 32 bit, tightdb 32 bit (on a 64 bit OS, but that shouldn't make a difference)
        [DllImport("tightdb_c_cs32", EntryPoint = "spec_add_column", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr spec_add_column32(IntPtr spechandle, IntPtr type,[MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport("tightdb_c_cs64", EntryPoint = "spec_add_column", CallingConvention = CallingConvention.Cdecl,CharSet = CharSet.Unicode)]
        private static extern UIntPtr spec_add_column64(IntPtr spechandle, IntPtr type, [MarshalAs(UnmanagedType.LPStr)] string name);

        public static long SpecAddColumn(Spec spec, DataType type, string name)
        {
            if (Is64Bit)
                return (long)spec_add_column64(spec.Handle, DataTypeToIntPtr (type), name);
            return (long)spec_add_column32(spec.Handle, DataTypeToIntPtr(type), name);
        }


        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_column_index", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr table_get_column_index32(IntPtr tablehandle, [MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_column_index", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr table_get_column_index64(IntPtr tablehandle, [MarshalAs(UnmanagedType.LPStr)] string name);

        //returns -1 if the column string does not match a column index
        public static long TableGetColumnIndex(Table  table, string name)
        {
            if (Is64Bit)
                return (long)table_get_column_index64(table.Handle, name);
            return (long)table_get_column_index32(table.Handle, name);
        }

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_column_index", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr tableView_get_column_index32(IntPtr tableViehandle, [MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_column_index", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr tableView_get_column_index64(IntPtr tableViewhandle, [MarshalAs(UnmanagedType.LPStr)] string name);

        public static long TableViewGetColumnIndex(TableView tableView, string name)
        {
            if (Is64Bit)
                return (long)tableView_get_column_index64(tableView.Handle, name);
            return (long)tableView_get_column_index32(tableView.Handle, name);
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "table_rename_column", CallingConvention = CallingConvention.Cdecl,CharSet = CharSet.Unicode)]
        private static extern void table_rename_column64(IntPtr tableHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_rename_column", CallingConvention = CallingConvention.Cdecl,CharSet = CharSet.Unicode)]
        private static extern void table_rename_column32(IntPtr tableHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)] string name);

        public static void TableRenameColumn(Table table, long columnIndex, string name)
        {
            if (Is64Bit)
                table_rename_column64(table.Handle,(IntPtr)columnIndex, name);
            else
                table_rename_column32(table.Handle, (IntPtr)columnIndex, name);
        }



        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_remove_column", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void table_remove_column64(IntPtr tableHandle, long columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_remove_column", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void table_remove_column32(IntPtr tableHandle, long columnIndex);

        public static void TableRemoveColumn(Table table, long columnIndex)
        {
            if (Is64Bit)
                table_remove_column64(table.Handle, columnIndex);
            else
                table_remove_column32(table.Handle, columnIndex);
        }





        [DllImport("tightdb_c_cs64", EntryPoint = "table_add_column", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern UIntPtr table_add_column64(IntPtr tableHandle, IntPtr type,
                                                         [MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_add_column", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern UIntPtr table_add_column32(IntPtr tableHandle, IntPtr type,
                                                         [MarshalAs(UnmanagedType.LPStr)] string name);

        public static long TableAddColumn(Table table, DataType type, string name)
        {
            if (Is64Bit)
                return (long)table_add_column64(table.Handle, DataTypeToIntPtr(type), name);
                    //BM told me that column number sb long always in C#            
            return (long)table_add_column32(table.Handle, DataTypeToIntPtr(type), name);
                //BM told me that column number sb long always in C#            
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "spec_get_column_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr spec_get_column_type64(IntPtr spechandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "spec_get_column_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr spec_get_column_type32(IntPtr spechandle, IntPtr columnIndex);

        public static DataType SpecGetColumnType(Spec s, long columnIndex)
        {
            if (Is64Bit)
                return IntPtrToDataType(spec_get_column_type64(s.Handle, (IntPtr)columnIndex));
            //down here we must be in 32 bit mode (or 16 bit or  more than 64 bit which is NOT supported yet
            if (columnIndex > Int32.MaxValue)
            {
                throw new ArgumentOutOfRangeException("columnIndex",String.Format(CultureInfo.InvariantCulture,"in 32 bit mode, column index must at most be {0} but SpecGetColumnType was called with{1}",Int32.MaxValue,columnIndex) );
            }
            return IntPtrToDataType(spec_get_column_type32(s.Handle, (IntPtr)columnIndex));
        }

        /* not really needed
        public static DataType spec_get_column_type(Spec s, int columnIndex)
        {
            return spec_get_column_type(s.SpecHandle, (IntPtr)columnIndex);//the IntPtr cast of an int works on 32bit .net 4.5
        }                                                                   //but probably throws an exception or warning on 64bit 
        */
        //Spec add_subtable_column(const char* name);        
        [DllImport("tightdb_c_cs64", EntryPoint = "spec_add_subtable_column",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr spec_add_subtable_column64(IntPtr spec,
                                                                [MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport("tightdb_c_cs32", EntryPoint = "spec_add_subtable_column",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr spec_add_subtable_column32(IntPtr spec,
                                                                [MarshalAs(UnmanagedType.LPStr)] string name);





        public static Spec AddSubTableColumn(Spec spec, String name)
        {
            if (name == null)
            {
                throw new ArgumentNullException("name",
                                                "Adding a sub table column with 'name' set to null is not allowed");
            }
            IntPtr specHandle = Is64Bit
                                    ? spec_add_subtable_column64(spec.Handle, name)
                                    : spec_add_subtable_column32(spec.Handle, name);

            return new Spec(specHandle, true); //because this spechandle we get here should be deallocated
        }

        //get a spec given a column index. Returns specs for subtables, but not for mixed (as they would need a row index too)
        //Spec       *spec_get_spec(Spec *spec, size_t column_ndx);
        [DllImport("tightdb_c_cs64", EntryPoint = "spec_get_spec", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr spec_get_spec64(IntPtr spec, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "spec_get_spec", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr spec_get_spec32(IntPtr spec, IntPtr columnIndex);


        private const string ErrColumnNotTable =
            "SpecGetSpec called with a column index for a column that is not a table";

        public static Spec SpecGetSpec(Spec spec, long columnIndex)
        {
            if (spec.GetColumnType(columnIndex) != DataType.Table)
            {
                throw new SpecException(ErrColumnNotTable);
            }
            if (Is64Bit)
                return new Spec(spec_get_spec64(spec.Handle, (IntPtr) columnIndex), true);
            return new Spec(spec_get_spec32(spec.Handle, (IntPtr) columnIndex), true);
        }

        /*not really needed
        public static Spec spec_get_spec(Spec spec, int ColumnIndex)
        {
            if (spec.get_column_type(ColumnIndex) == DataType.Table)
            {
                IntPtr SpecHandle = spec_get_spec((IntPtr)spec.SpecHandle, (IntPtr)ColumnIndex);
                return new Spec(SpecHandle, true);
            }
            else
                throw new SpecException(err_column_not_table);
        }*/


        //size_t spec_get_column_count(Spec* spec);
        [DllImport("tightdb_c_cs64", EntryPoint = "spec_get_column_count", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr spec_get_column_count64(IntPtr spec);

        [DllImport("tightdb_c_cs32", EntryPoint = "spec_get_column_count", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr spec_get_column_count32(IntPtr spec);


        public static long SpecGetColumnCount(Spec spec)
        {
            if (Is64Bit)
                return (long) spec_get_column_count64(spec.Handle);
            //OPTIMIZE  - could be optimized with a 32 or 64 bit specific implementation - see end of file
            return (long) spec_get_column_count32(spec.Handle);
            //OPTIMIZE  - could be optimized with a 32 or 64 bit specific implementation - see end of file
        }


        //tightdb_c_cs_API size_t new_table()
        [DllImport("tightdb_c_cs64", EntryPoint = "new_table", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr new_table64();

        [DllImport("tightdb_c_cs32", EntryPoint = "new_table", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr new_table32();

        public static void TableNew(Table table)
        {
            table.SetHandle(Is64Bit ? new_table64() : new_table32(), true);
        }

        
        [DllImport("tightdb_c_cs64", EntryPoint = "group_write", CallingConvention = CallingConvention.Cdecl)]
        private static extern void group_write64(IntPtr groupPtr  , [MarshalAs(UnmanagedType.LPStr)] string fileName);

        [DllImport("tightdb_c_cs32", EntryPoint = "group_write", CallingConvention = CallingConvention.Cdecl)]
        private static extern void group_write32(IntPtr groupPTr, [MarshalAs(UnmanagedType.LPStr)] string fileName);

        //todo:test and add OS file operation error handling
        public static void GroupWrite(Group group, string fileName)
        {
            if (Is64Bit) group_write64(group.Handle,fileName);
            else
                group_write32(group.Handle, fileName);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "new_group_file", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr new_group_file64([MarshalAs(UnmanagedType.LPStr)] string fileName);

        [DllImport("tightdb_c_cs32", EntryPoint = "new_group_file", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr new_group_file32([MarshalAs(UnmanagedType.LPStr)] string fileName);


        public static void GroupNewFile(Group group, string fileName)
        {
            Console.Error.WriteLine("groupnewfile called");
            group.SetHandle(Is64Bit
                                ? new_group_file64(fileName)
                                : new_group_file32(fileName), true);
            Console.Error.WriteLine("after new_group_file");
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "new_group", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr new_group64();

        [DllImport("tightdb_c_cs32", EntryPoint = "new_group", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr new_group32();


        public static void GroupNew(Group group)
        {
           // Console.WriteLine("In GroupNew, about to call group.sethandle(call to new_group)");
            group.SetHandle(Is64Bit
                                ? new_group64()
                                : new_group32(), true);
            //Console.WriteLine("Group got handle {0}", group.ObjectIdentification());
        }




        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_size", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableview_get_size64(IntPtr tablePtr);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_size", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableview_get_size32(IntPtr tablePtr);

        public static long TableViewSize(TableView tv)
        {
            if (Is64Bit)
                return (long) tableview_get_size64(tv.Handle);
            return (long) tableview_get_size32(tv.Handle);
        }






        //size_t         find_first_int(size_t column_ndx, int64_t value) const;
        //TIGHTDB_C_CS_API size_t table_find_first_int(Table * table_ptr , size_t column_ndx, int64_t value)

        [DllImport("tightdb_c_cs64", EntryPoint = "table_find_first_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_int64(IntPtr tableHandle, IntPtr columnIndex, long value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_find_first_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_int32(IntPtr tableHandle, IntPtr columnIndex, long value);


        public static long TableFindFirstInt(Table table, long columnIndex, long value)
        {
            return                
                    Is64Bit
                        ? (long)table_find_first_int64(table.Handle, (IntPtr)columnIndex, value)
                        : (long)table_find_first_int32(table.Handle, (IntPtr)columnIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_find_first_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_string64(IntPtr tableHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_find_first_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_string32(IntPtr tableHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)] string value);


        public static long TableFindFirstString(Table table, long columnIndex, string value)
        {
            return
                    Is64Bit
                        ? (long)table_find_first_string64(table.Handle, (IntPtr)columnIndex, value)
                        : (long)table_find_first_string32(table.Handle, (IntPtr)columnIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_find_first_binary", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_binary64(IntPtr tableHandle, IntPtr columnIndex, byte[] value,IntPtr length);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_find_first_binary", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_binary32(IntPtr tableHandle, IntPtr columnIndex, byte[] value,IntPtr length);


        public static long TableFindFirstBinary(Table table, long columnIndex, byte[] value)
        {
            return
                    Is64Bit
                        ? (long)table_find_first_binary64(table.Handle, (IntPtr)columnIndex, value,(IntPtr)value.Length)
                        : (long)table_find_first_binary32(table.Handle, (IntPtr)columnIndex, value,(IntPtr)value.Length);
        }




        

        [DllImport("tightdb_c_cs64", EntryPoint = "table_find_first_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_double64(IntPtr tableHandle, IntPtr columnIndex, double value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_find_first_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_double32(IntPtr tableHandle, IntPtr columnIndex, double value);


        public static long TableFindFirstDouble(Table table, long columnIndex, double value)
        {
            return
                    Is64Bit
                        ? (long)table_find_first_double64(table.Handle, (IntPtr)columnIndex, value)
                        : (long)table_find_first_double32(table.Handle, (IntPtr)columnIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_find_first_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_float64(IntPtr tableHandle, IntPtr columnIndex, float value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_find_first_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_float32(IntPtr tableHandle, IntPtr columnIndex, float value);


        public static long TableFindFirstFloat(Table table, long columnIndex, float value)
        {
            return
                    Is64Bit
                        ? (long)table_find_first_float64(table.Handle, (IntPtr)columnIndex, value)
                        : (long)table_find_first_float32(table.Handle, (IntPtr)columnIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_find_first_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_date64(IntPtr tableHandle, IntPtr columnIndex, Int64 value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_find_first_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_date32(IntPtr tableHandle, IntPtr columnIndex, Int64 value);


        public static long TableFindFirstDate(Table table, long columnIndex, DateTime value)
        {
            return
                    Is64Bit
                        ? (long)table_find_first_date64(table.Handle, (IntPtr)columnIndex, ToTightDbTime(value))
                        : (long)table_find_first_date32(table.Handle, (IntPtr)columnIndex, ToTightDbTime(value));
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_find_first_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_bool64(IntPtr tableHandle, IntPtr columnIndex, IntPtr value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_find_first_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_first_bool32(IntPtr tableHandle, IntPtr columnIndex, IntPtr value);


        public static long TableFindFirstBool(Table table, long columnIndex, bool value)
        {
            return
                    Is64Bit
                        ? (long)table_find_first_bool64(table.Handle, (IntPtr)columnIndex, BoolToIntPtr(value))
                        : (long)table_find_first_bool32(table.Handle, (IntPtr)columnIndex, BoolToIntPtr(value));
        }




        //find first in tableview
        //size_t         find_first_int(size_t column_ndx, int64_t value) const;
        //TIGHTDB_C_CS_API size_t table_find_first_int(Table * table_ptr , size_t column_ndx, int64_t value)

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_find_first_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_int64(IntPtr tableViewHandle, IntPtr columnIndex, long value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_find_first_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_int32(IntPtr tableViewHandle, IntPtr columnIndex, long value);


        public static long TableViewFindFirstInt(TableView tableView, long columnIndex, long value)
        {
            return
                    Is64Bit
                        ? (long)tableView_find_first_int64(tableView.Handle, (IntPtr)columnIndex, value)
                        : (long)tableView_find_first_int32(tableView.Handle, (IntPtr)columnIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_find_first_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_string64(IntPtr tableViewHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_find_first_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_string32(IntPtr tableViewHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)] string value);


        public static long TableViewFindFirstString(TableView tableView, long columnIndex, string value)
        {
            return
                    Is64Bit
                        ? (long)tableView_find_first_string64(tableView.Handle, (IntPtr)columnIndex, value)
                        : (long)tableView_find_first_string32(tableView.Handle, (IntPtr)columnIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_find_first_binary", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_binary64(IntPtr tableViewHandle, IntPtr columnIndex, byte[] value, IntPtr length);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_find_first_binary", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_binary32(IntPtr tableViewHandle, IntPtr columnIndex, byte[] value, IntPtr length);


        public static long TableViewFindFirstBinary(TableView tableView, long columnIndex, byte[] value)
        {
            return
                    Is64Bit
                        ? (long)tableView_find_first_binary64(tableView.Handle, (IntPtr)columnIndex, value, (IntPtr)value.Length)
                        : (long)tableView_find_first_binary32(tableView.Handle, (IntPtr)columnIndex, value, (IntPtr)value.Length);
        }






        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_find_first_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_double64(IntPtr tableViewHandle, IntPtr columnIndex, double value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_find_first_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_double32(IntPtr tableViewHandle, IntPtr columnIndex, double value);


        public static long TableViewFindFirstDouble(TableView tableView, long columnIndex, double value)
        {
            return
                    Is64Bit
                        ? (long)tableView_find_first_double64(tableView.Handle, (IntPtr)columnIndex, value)
                        : (long)tableView_find_first_double32(tableView.Handle, (IntPtr)columnIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_find_first_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_float64(IntPtr tableViewHandle, IntPtr columnIndex, float value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_find_first_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_float32(IntPtr tableViewHandle, IntPtr columnIndex, float value);


        public static long TableViewFindFirstFloat(TableView tableView, long columnIndex, float value)
        {
            return
                    Is64Bit
                        ? (long)tableView_find_first_float64(tableView.Handle, (IntPtr)columnIndex, value)
                        : (long)tableView_find_first_float32(tableView.Handle, (IntPtr)columnIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_find_first_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_date64(IntPtr tableViewHandle, IntPtr columnIndex, Int64 value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_find_first_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_date32(IntPtr tableViewHandle, IntPtr columnIndex, Int64 value);


        public static long TableViewFindFirstDate(TableView tableView, long columnIndex, DateTime value)
        {
            return
                    Is64Bit
                        ? (long)tableView_find_first_date64(tableView.Handle, (IntPtr)columnIndex, ToTightDbTime(value))
                        : (long)tableView_find_first_date32(tableView.Handle, (IntPtr)columnIndex, ToTightDbTime(value));
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_find_first_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_bool64(IntPtr tableViewHandle, IntPtr columnIndex, IntPtr value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_find_first_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_first_bool32(IntPtr tableViewHandle, IntPtr columnIndex, IntPtr value);


        public static long TableViewFindFirstBool(TableView tableView, long columnIndex, bool value)
        {
            return
                    Is64Bit
                        ? (long)tableView_find_first_bool64(tableView.Handle, (IntPtr)columnIndex, BoolToIntPtr(value))
                        : (long)tableView_find_first_bool32(tableView.Handle, (IntPtr)columnIndex, BoolToIntPtr(value));
        }





        [DllImport("tightdb_c_cs64", EntryPoint = "table_distinct", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_distinct64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_distinct", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_distinct32(IntPtr tableHandle, IntPtr columnIndex);


        public static TableView TableDistinct(Table table, long columnIndex)
        {
            return
                new TableView(
                    Is64Bit
                        ? table_distinct64(table.Handle, (IntPtr)columnIndex)
                        : table_distinct32(table.Handle, (IntPtr)columnIndex), true);
        }





        [DllImport("tightdb_c_cs64", EntryPoint = "table_count_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_count_int64(IntPtr tableHandle, IntPtr columnIndex,long target);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_count_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_count_int32(IntPtr tableHandle, IntPtr columnIndex, long target);


        public static long TableCountLong(Table table, long columnIndex,long target)
        {
            if (Is64Bit)
                return table_count_int64(table.Handle, (IntPtr)columnIndex,target);
            return table_count_int32(table.Handle, (IntPtr)columnIndex,target);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "table_count_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_count_float64(IntPtr tableHandle, IntPtr columnIndex,float target);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_count_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_count_float32(IntPtr tableHandle, IntPtr columnIndex,float target);


        public static long TableCountFloat(Table table, long columnIndex,float target)
        {
            if (Is64Bit)
                return table_count_float64(table.Handle, (IntPtr)columnIndex,target);
            return table_count_float32(table.Handle, (IntPtr)columnIndex,target);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "table_count_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_count_double64(IntPtr tableHandle, IntPtr columnIndex,double target);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_count_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_count_double32(IntPtr tableHandle, IntPtr columnIndex,double target);


        public static long TableCountDouble(Table table, long columnIndex,double target)
        {
            if (Is64Bit)
                return table_count_double64(table.Handle, (IntPtr)columnIndex,target);
            return table_count_double32(table.Handle, (IntPtr)columnIndex,target);
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "table_count_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_count_string64(IntPtr tableHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)] string target);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_count_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_count_string32(IntPtr tableHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)]string target);


        public static long TableCountString(Table table, long columnIndex, string target)
        {
            if (Is64Bit)
                return table_count_string64(table.Handle, (IntPtr)columnIndex, target);
            return table_count_string32(table.Handle, (IntPtr)columnIndex, target);
        }







        [DllImport("tightdb_c_cs64", EntryPoint = "table_sum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_sum_int64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_sum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_sum_int32(IntPtr tableHandle, IntPtr columnIndex);


        public static long TableSumLong(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_sum_int64(table.Handle, (IntPtr)columnIndex);
            return table_sum_int32(table.Handle, (IntPtr)columnIndex);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "table_sum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_sum_float64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_sum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_sum_float32(IntPtr tableHandle, IntPtr columnIndex);


        public static float TableSumFloat(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_sum_float64(table.Handle, (IntPtr)columnIndex);
            return table_sum_float32(table.Handle, (IntPtr)columnIndex);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "table_sum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_sum_double64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_sum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_sum_double32(IntPtr tableHandle, IntPtr columnIndex);


        public static double TableSumDouble(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_sum_double64(table.Handle, (IntPtr)columnIndex);
            return table_sum_double32(table.Handle, (IntPtr)columnIndex);
        }








        [DllImport("tightdb_c_cs64", EntryPoint = "table_maximum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_maximum64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_maximum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_maximum32(IntPtr tableHandle, IntPtr columnIndex);


        public static long TableMaximumLong(Table table, long columnIndex)
        {
            if (Is64Bit)           
                return table_maximum64(table.Handle, (IntPtr) columnIndex);            
            return table_maximum32(table.Handle, (IntPtr) columnIndex);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "table_maximum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_maximum_float64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_maximum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_maximum_float32(IntPtr tableHandle, IntPtr columnIndex);


        public static float TableMaximumFloat(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_maximum_float64(table.Handle, (IntPtr)columnIndex);
            return table_maximum_float32(table.Handle, (IntPtr)columnIndex);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "table_maximum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_maximum_double64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_maximum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_maximum_double32(IntPtr tableHandle, IntPtr columnIndex);


        public static double TableMaximumDouble(Table table, long columnIndex)
        {
            
            if (Is64Bit)
                return table_maximum_double64(table.Handle, (IntPtr)columnIndex);
            return table_maximum_double32(table.Handle, (IntPtr)columnIndex);
        }







        [DllImport("tightdb_c_cs64", EntryPoint = "table_minimum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_minimum64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_minimum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_minimum32(IntPtr tableHandle, IntPtr columnIndex);


        public static long TableMinimum(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_minimum64(table.Handle, (IntPtr)columnIndex);
            return table_minimum32(table.Handle, (IntPtr)columnIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_minimum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_minimum_float64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_minimum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_minimum_float32(IntPtr tableHandle, IntPtr columnIndex);


        public static float TableMinimumFloat(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_minimum_float64(table.Handle, (IntPtr)columnIndex);
            return table_minimum_float32(table.Handle, (IntPtr)columnIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_minimum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_minimum_double64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_minimum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_minimum_double32(IntPtr tableHandle, IntPtr columnIndex);


        public static double TableMinimumDouble(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_minimum_double64(table.Handle, (IntPtr)columnIndex);
            return table_minimum_double32(table.Handle, (IntPtr)columnIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_average_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_average64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_average_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_average32(IntPtr tableHandle, IntPtr columnIndex);


        public static double TableAverage(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_average64(table.Handle, (IntPtr)columnIndex);
            return table_average32(table.Handle, (IntPtr)columnIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_average_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_average_float64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_average_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_average_float32(IntPtr tableHandle, IntPtr columnIndex);


        public static double TableAverageFloat(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_average_float64(table.Handle, (IntPtr)columnIndex);
            return table_average_float32(table.Handle, (IntPtr)columnIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_average_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_average_double64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_average_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double table_average_double32(IntPtr tableHandle, IntPtr columnIndex);


        public static double TableAverageDouble(Table table, long columnIndex)
        {
            if (Is64Bit)
                return table_average_double64(table.Handle, (IntPtr)columnIndex);
            return table_average_double32(table.Handle, (IntPtr)columnIndex);
        }








        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_maximum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_maximum64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_maximum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_maximum32(IntPtr tableHandle, IntPtr columnIndex);


        public static long TableViewMaximum(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_maximum64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_maximum32(tableView.Handle, (IntPtr)columnIndex);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_maximum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableview_maximum_float64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_maximum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableview_maximum_float32(IntPtr tableHandle, IntPtr columnIndex);


        public static float TableViewMaximumFloat(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_maximum_float64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_maximum_float32(tableView.Handle, (IntPtr)columnIndex);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_maximum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_maximum_double64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_maximum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_maximum_double32(IntPtr tableHandle, IntPtr columnIndex);


        public static double TableViewMaximumDouble(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_maximum_double64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_maximum_double32(tableView.Handle, (IntPtr)columnIndex);
        }







        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_minimum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_minimum64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_minimum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_minimum32(IntPtr tableHandle, IntPtr columnIndex);


        public static long TableViewMinimumLong(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_minimum64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_minimum32(tableView.Handle, (IntPtr)columnIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_minimum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableview_minimum_float64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_minimum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableview_minimum_float32(IntPtr tableHandle, IntPtr columnIndex);


        public static float TableViewMinimumFloat(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_minimum_float64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_minimum_float32(tableView.Handle, (IntPtr)columnIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_minimum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_minimum_double64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_minimum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_minimum_double32(IntPtr tableHandle, IntPtr columnIndex);


        public static double TableViewMinimumDouble(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_minimum_double64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_minimum_double32(tableView.Handle, (IntPtr)columnIndex);
        }







        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_average_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_average64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_average_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_average32(IntPtr tableHandle, IntPtr columnIndex);


        public static double TableViewAverageLong(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_average64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_average32(tableView.Handle, (IntPtr)columnIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_average_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_average_float64(IntPtr tableViewHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_average_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_average_float32(IntPtr tableViewHandle, IntPtr columnIndex);


        public static double TableViewAverageFloat(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_average_float64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_average_float32(tableView.Handle, (IntPtr)columnIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_average_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_average_double64(IntPtr tableViewHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_average_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_average_double32(IntPtr tableViewHandle, IntPtr columnIndex);


        public static double TableViewAverageDouble(TableView tableview, long columnIndex)
        {
            if (Is64Bit)
                return tableview_average_double64(tableview.Handle, (IntPtr)columnIndex);
            return tableview_average_double32(tableview.Handle, (IntPtr)columnIndex);
        }








        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_sum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_sum64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_sum_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_sum32(IntPtr tableHandle, IntPtr columnIndex);


        public static long TableViewSumLong(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_sum64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_sum32(tableView.Handle, (IntPtr)columnIndex);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_sum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableview_sum_float64(IntPtr tableViewHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_sum_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableview_sum_float32(IntPtr tableViewHandle, IntPtr columnIndex);


        public static float TableViewSumFloat(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_sum_float64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_sum_float32(tableView.Handle, (IntPtr)columnIndex);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_sum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_sum_double64(IntPtr tableViewHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_sum_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double tableview_sum_double32(IntPtr tableViewHandle, IntPtr columnIndex);


        public static double TableViewSumDouble(TableView tableView, long columnIndex)
        {
            if (Is64Bit)
                return tableview_sum_double64(tableView.Handle, (IntPtr)columnIndex);
            return tableview_sum_double32(tableView.Handle, (IntPtr)columnIndex);
        }





        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_count_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_count_int64(IntPtr tableViewHandle, IntPtr columnIndex, long target);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_count_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_count_int32(IntPtr tableViewHandle, IntPtr columnIndex, long target);


        public static long TableViewCountLong(TableView tableView, long columnIndex, long target)
        {
            if (Is64Bit)
                return tableview_count_int64(tableView.Handle, (IntPtr)columnIndex, target);
            return tableview_count_int32(tableView.Handle, (IntPtr)columnIndex, target);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_count_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_count_float64(IntPtr tableViewHandle, IntPtr columnIndex, float target);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_count_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_count_float32(IntPtr tableViewHandle, IntPtr columnIndex, float target);


        public static long TableViewCountFloat(TableView tableView, long columnIndex, float target)
        {
            if (Is64Bit)
                return tableview_count_float64(tableView.Handle, (IntPtr)columnIndex, target);
            return tableview_count_float32(tableView.Handle, (IntPtr)columnIndex, target);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_count_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_count_double64(IntPtr tableViewHandle, IntPtr columnIndex, double target);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_count_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_count_double32(IntPtr tableViewHandle, IntPtr columnIndex, double target);


        public static long TableViewCountDouble(TableView tableView, long columnIndex, double target)
        {
            if (Is64Bit)
                return tableview_count_double64(tableView.Handle, (IntPtr)columnIndex, target);
            return tableview_count_double32(tableView.Handle, (IntPtr)columnIndex, target);
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_count_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_count_string64(IntPtr tableViewHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)] string target);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_count_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableview_count_string32(IntPtr tableViewHandle, IntPtr columnIndex, [MarshalAs(UnmanagedType.LPStr)]string target);


        public static long TableViewCountString(TableView tableView, long columnIndex, string target)
        {
            if (Is64Bit)
                return tableview_count_string64(tableView.Handle, (IntPtr)columnIndex, target);
            return tableview_count_string32(tableView.Handle, (IntPtr)columnIndex, target);
        }






















        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_index", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_index64(IntPtr tableHandle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_index", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_index32(IntPtr tableHandle, IntPtr columnIndex);


        public static void TableSetIndex(Table table, long columnIndex)
        {
            if (Is64Bit)

                table_set_index64(table.Handle, (IntPtr)columnIndex);
            else
                table_set_index32(table.Handle, (IntPtr)columnIndex);
        }















        [DllImport("tightdb_c_cs64", EntryPoint = "table_find_all_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_all_int64(IntPtr tableHandle, IntPtr columnIndex, long value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_find_all_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_all_int32(IntPtr tableHandle, IntPtr columnIndex, long value);


        public static TableView TableFindAllInt(Table table, long columnIndex, long value)
        {
            return
                new TableView(
                    Is64Bit
                        ? table_find_all_int64(table.Handle, (IntPtr) columnIndex, value)
                        : table_find_all_int32(table.Handle, (IntPtr) columnIndex, value), true);
        }


        //TIGHTDB_C_CS_API tightdb::TableView* table_find_all_int(Table * table_ptr , size_t column_ndx, int64_t value)
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_find_all_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_all_int64(IntPtr tableViewHandle, IntPtr columnIndex, long value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_find_all_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_all_int32(IntPtr tableViewHandle, IntPtr columnIndex, long value);


        public static TableView TableViewFindAllInt(TableView tableView, long columnIndex, long value)
        {
            return
                new TableView(
                    Is64Bit
                        ? tableView_find_all_int64(tableView.Handle, (IntPtr)columnIndex, value)
                        : tableView_find_all_int32(tableView.Handle, (IntPtr)columnIndex, value), true);
        }






        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_find_all_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_all_string64(IntPtr tableHandle, IntPtr columnIndex, string value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_find_all_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_find_all_string32(IntPtr tableHandle, IntPtr columnIndex, string value);


        public static TableView TableFindAllString(Table table, long columnIndex, string value)
        {
            return
                new TableView(
                    Is64Bit
                        ? table_find_all_string64(table.Handle, (IntPtr)columnIndex, value)
                        : table_find_all_string32(table.Handle, (IntPtr)columnIndex, value), true);
        }



        //TIGHTDB_C_CS_API tightdb::TableView* table_find_all_int(Table * table_ptr , size_t column_ndx, int64_t value)
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_find_all_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_all_string64(IntPtr tableViewHandle, IntPtr columnIndex, string value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_find_all_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_find_all_string32(IntPtr tableViewHandle, IntPtr columnIndex, string value);


        public static TableView TableViewFindAllString(TableView tableView, long columnIndex, string value)
        {
            return
                new TableView(
                    Is64Bit
                        ? tableView_find_all_string64(tableView.Handle, (IntPtr)columnIndex, value)
                        : tableView_find_all_string32(tableView.Handle, (IntPtr)columnIndex, value), true);
        }







        //        TIGHTDB_C_CS_API tightdb::TableView* query_find_all_int(Query * query_ptr , size_t start, size_t end, size_t limit)
        [DllImport("tightdb_c_cs64", EntryPoint = "query_find_all", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr query_find_all64(IntPtr handle, IntPtr start, IntPtr end, IntPtr limit);

        [DllImport("tightdb_c_cs32", EntryPoint = "query_find_all", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr query_find_all32(IntPtr handle, IntPtr start, IntPtr end, IntPtr limit);


        public static TableView QueryFindAll(Query query, long start, long end, long limit)
        {
            return
                new TableView(
                    Is64Bit
                        ? query_find_all64(query.Handle, (IntPtr) start, (IntPtr) end, (IntPtr) limit)
                        : query_find_all32(query.Handle, (IntPtr) start, (IntPtr) end, (IntPtr) limit), true);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "query_find_next", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr query_find_next64(IntPtr handle,IntPtr lastMatch);

        [DllImport("tightdb_c_cs32", EntryPoint = "query_find_next", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr query_find_next32(IntPtr handle, IntPtr lastMatch);


        public static long QueryFindNext(Query query,long lastMatch)
        {
            return                
                    Is64Bit
                        ? (long)query_find_next64(query.Handle,(IntPtr) lastMatch)
                        : (long)query_find_next32(query.Handle,(IntPtr) lastMatch);
        }



        
        [DllImport("tightdb_c_cs64", EntryPoint = "query_average", CallingConvention = CallingConvention.Cdecl)]
        private static extern double query_average64(IntPtr handle, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "query_average", CallingConvention = CallingConvention.Cdecl)]
        private static extern double query_average32(IntPtr handle, IntPtr columnIndex);


        public static double QueryAverage(Query query, long columnIndex)
        {
            return
                    Is64Bit
                        ? query_average64(query.Handle, (IntPtr)columnIndex)
                        : query_average32(query.Handle, (IntPtr)columnIndex);
        }

        





        //this one work on standard subtable columns as well as on mixed subtable columns.
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_subtable", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_subtable64(IntPtr tableHandle, IntPtr columnIndex, IntPtr rowIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_subtable", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_subtable32(IntPtr tableHandle, IntPtr columnIndex, IntPtr rowIndex);

        public static Table TableGetSubTable(Table parentTable, long columnIndex, long rowIndex)
        {
            if (Is64Bit)
                return new Table(
                    table_get_subtable64(parentTable.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex), true);
            
            return new Table(table_get_subtable32(parentTable.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex),
                             true);
        }




        //If the name exists in the group, the table associated with the name is returned
        //if the name does not exist in the group, a new table is created and returned
        [DllImport("tightdb_c_cs64", EntryPoint = "group_get_table", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr group_get_table64(IntPtr groupHandle, [MarshalAs(UnmanagedType.LPStr)] String tableName);

        [DllImport("tightdb_c_cs32", EntryPoint = "group_get_table", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr group_get_table32(IntPtr groupHandle, [MarshalAs(UnmanagedType.LPStr)] String tableName);

        public static Table GroupGetTable(Group group, string tableName)
        {
            if (Is64Bit)
                return new Table(group_get_table64(group.Handle, tableName), true);            
            return new Table(group_get_table32(group.Handle, tableName),true);
        }


        
        //
        [DllImport("tightdb_c_cs64", EntryPoint = "group_has_table", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr group_has_table64(IntPtr groupHandle, [MarshalAs(UnmanagedType.LPStr)] String tableName);

        [DllImport("tightdb_c_cs32", EntryPoint = "group_has_table", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr group_has_table32(IntPtr groupHandle, [MarshalAs(UnmanagedType.LPStr)] String tableName);

        public static bool GroupHassTable(Group group, string tableName)
        {
            if (Is64Bit)
                return IntPtrToBool( group_has_table64(group.Handle, tableName));
            return IntPtrToBool(group_has_table32(group.Handle, tableName));
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_subtable", CallingConvention = CallingConvention.Cdecl)
        ]
        private static extern IntPtr tableView_get_subtable64(IntPtr tableHandle, IntPtr columnIndex, IntPtr rowIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_subtable", CallingConvention = CallingConvention.Cdecl)
        ]
        private static extern IntPtr tableView_get_subtable32(IntPtr tableHandle, IntPtr columnIndex, IntPtr rowIndex);

        //note this also should work on mixed columns with subtables in them
        public static Table TableViewGetSubTable(TableView parentTableView, long columnIndex, long rowIndex)
        {
            if (Is64Bit)
                return new Table(
                    tableView_get_subtable64(parentTableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex), true);
            //the constructor that takes an UintPtr will use that as a table handle
            return new Table(tableView_get_subtable32(parentTableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex),
                             true);
        }




        //tightdb_c_cs_API void unbind_table_ref(const size_t TablePtr)

        [DllImport("tightdb_c_cs64", EntryPoint = "unbind_table_ref", CallingConvention = CallingConvention.Cdecl)]
        private static extern void unbind_table_ref64(IntPtr tableHandle);

        [DllImport("tightdb_c_cs32", EntryPoint = "unbind_table_ref", CallingConvention = CallingConvention.Cdecl)]
        private static extern void unbind_table_ref32(IntPtr tableHandle);

        //      void    table_unbind(const Table *t); /* Ref-count delete of table* from table_get_table() */
        public static void TableUnbind(Table t)
        {
            //Console.WriteLine("Tableunbind calling unbind_table_ref " + t.ObjectIdentification());
            if (Is64Bit)
                unbind_table_ref64(t.Handle);
            else
                unbind_table_ref32(t.Handle);
            t.Handle = IntPtr.Zero;
            //Console.WriteLine("Tableunbind called unbind_table_ref " + t.ObjectIdentification());
        }



        //TIGHTDB_C_CS_API void tableview_delete(TableView * tableview_ptr )

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_delete", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_delete64(IntPtr tableViewHandle);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_delete", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_delete32(IntPtr tableViewHandle);

        //      void    table_unbind(const Table *t); /* Ref-count delete of table* from table_get_table() */
        public static void TableViewUnbind(TableView tv)
        {
            //Console.WriteLine("TableViewUnbind calling tableview_delete " + tv.ObjectIdentification());
            if (Is64Bit)
                tableview_delete64(tv.Handle);
            else
                tableview_delete32(tv.Handle);
            tv.Handle = IntPtr.Zero;
            //Console.WriteLine("TableViewUnbind called tableview_delete " + tv.ObjectIdentification());
        }




        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_remove", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_remove64(IntPtr tableViewHandle,long rowIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_remove", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_remove32(IntPtr tableViewHandle,long rowIndex);

        //      void    table_unbind(const Table *t); /* Ref-count delete of table* from table_get_table() */
        public static void TableViewRemove(TableView tv,long rowIndex)
        {
         //   Console.WriteLine("TableViewRemove calling tableview_remove " + tv.ObjectIdentification());
            if (Is64Bit)
                tableview_remove64(tv.Handle,rowIndex);
            else
                tableview_remove32(tv.Handle,rowIndex);
            tv.Handle = IntPtr.Zero;
           // Console.WriteLine("TableViewRemove called tableview_remove " + tv.ObjectIdentification());
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "table_remove_row", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_remove_row64(IntPtr tableHandle, long rowIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_remove_row", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_remove_row32(IntPtr tableHandle, long rowIndex);
       
        public static void TableRemove(Table t, long rowIndex)
        {
            
            if (Is64Bit)
                table_remove_row64(t.Handle, rowIndex);
            else
                table_remove_row32(t.Handle, rowIndex);
                        
        }








        [DllImport("tightdb_c_cs64", EntryPoint = "query_delete", CallingConvention = CallingConvention.Cdecl)]
        private static extern void query_delete64(IntPtr handle);

        [DllImport("tightdb_c_cs32", EntryPoint = "query_delete", CallingConvention = CallingConvention.Cdecl)]
        private static extern void query_delete32(IntPtr handle);

        public static void QueryDelete(Query q)
        {
            //Console.WriteLine("QueryDelete calling query_delete " + q.ObjectIdentification());

            if (Is64Bit)
                query_delete64(q.Handle);
            else
                query_delete32(q.Handle);
            q.Handle = IntPtr.Zero;
            //Console.WriteLine("QueryDelete called query_delete " + q.ObjectIdentification());
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "group_delete", CallingConvention = CallingConvention.Cdecl)]
        private static extern void group_delete64(IntPtr handle);

        [DllImport("tightdb_c_cs32", EntryPoint = "group_delete", CallingConvention = CallingConvention.Cdecl)]
        private static extern void group_delete32(IntPtr handle);

        public static void GroupDelete(Group g)
        {
        //    Console.WriteLine("group delete calling group_delete " + g.ObjectIdentification());           
                if (Is64Bit)
                    group_delete64(g.Handle);
                else
                    group_delete32(g.Handle);
            
            g.Handle = IntPtr.Zero;
        //    Console.WriteLine("group delete called group_delete " + g.ObjectIdentification());
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "test_testacquireanddeletegroup",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void test_testacquireanddeletegroup64();

        [DllImport("tightdb_c_cs32", EntryPoint = "test_testacquireanddeletegroup",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void test_testacquireanddeletegroup32();

        public static void test_testacquireanddeletegroup()
        {
            if (Is64Bit)
                test_testacquireanddeletegroup64();
            else
            test_testacquireanddeletegroup32();
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "table_where", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_where64(IntPtr handle);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_where", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_where32(IntPtr handle);


        public static Query table_where(Table table)
        {
            return new Query(Is64Bit ? table_where64(table.Handle) : table_where32(table.Handle),table, true);
        }



        // tightdb_c_cs_API size_t table_get_spec(size_t TablePtr)
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_spec", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_spec64(IntPtr tableHandle);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_spec", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_spec32(IntPtr tableHandle);


        //the spec returned here is live as long as the table itself is live, so don't dispose of the table and keep on using the spec
        public static Spec TableGetSpec(Table t)
        {
            if (Is64Bit)
                return new Spec(table_get_spec64(t.Handle), false);
            //this spec should NOT be deallocated after use 
            return new Spec(table_get_spec32(t.Handle), false);
            //this spec should NOT be deallocated after use         
        }


        //tightdb_c_cs_API size_t get_column_count(tightdb::Table* TablePtr)


        //            size_t      table_get_column_count(const Table *t);        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_column_count", CallingConvention = CallingConvention.Cdecl)
        ]
        private static extern IntPtr table_get_column_count64(IntPtr tableHandle);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_column_count", CallingConvention = CallingConvention.Cdecl)
        ]
        private static extern IntPtr table_get_column_count32(IntPtr tableHandle);

        public static long TableGetColumnCount(Table t)
        {
            if (Is64Bit)
                return (long) table_get_column_count64(t.Handle);
            return (long) table_get_column_count32(t.Handle);
        }





        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_column_count",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_get_column_count64(IntPtr tableHandle);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_column_count",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_get_column_count32(IntPtr tableHandle);

        public static long TableViewGetColumnCount(TableView t)
        {
            if (Is64Bit)
                return (long) tableView_get_column_count64(t.Handle);
            return (long) tableView_get_column_count32(t.Handle);
        }




        //table_get_column_name and spec_get_column_name have some buffer stuff in common. Consider refactoring. Note the DLL method call is
        //different in the two, and one function takes a spec, the other a table


        //tightdb_c_cs_API int get_column_name(Table* TablePtr,size_t column_ndx,char * colname, int bufsize)

        //Ignore comment below. Doesn't work wit MarshalAs for some reason
        //the MarshalAs LPTStr is set to let c# know that its 16 bit UTF-16 characters should be fit into a char* that uses 8 bit ANSI strings
        //In theory we *could* add a method to the DLL that takes and gives UTF-16 or similar, and a function that tells c# if UTF-16 is supported
        //on the c++ side on this platform. marshalling UTF-16 to UTF-16 will result in a buffer copy being saved, c++ would get a pointer directly into the stringbuilder buffer
        // [MarshalAs(UnmanagedType.LPTStr)]

        //            const char *table_get_column_name(const Table *t, size_t ndx);
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_column_name", CallingConvention = CallingConvention.Cdecl,CharSet = CharSet.Unicode)]
        private static extern IntPtr table_get_column_name64(IntPtr tableHandle, IntPtr columnIndex,[MarshalAs(UnmanagedType.LPStr)] StringBuilder name,IntPtr bufsize);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_column_name", CallingConvention = CallingConvention.Cdecl,CharSet = CharSet.Unicode)]
        private static extern IntPtr table_get_column_name32(IntPtr tableHandle, IntPtr columnIndex,[MarshalAs(UnmanagedType.LPStr)] StringBuilder name,IntPtr bufsize);


        public static string TableGetColumnName(Table t, long columnIndex)
            //ColumnIndex not a long bc on the c++ side it might be 32bit long on a 32 bit platform
        {
            var b = new StringBuilder(16);
            //string builder 16 is just a wild guess that most fields are shorter than this
            bool loop = true;
            do
            {
                int bufszneed;
                if (Is64Bit)
                    bufszneed =
                        (int) table_get_column_name64(t.Handle, (IntPtr) columnIndex, b, (IntPtr) b.Capacity);
                    //the intptr cast of the long *might* loose the high 32 bits on a 32 bits platform as tight c++ will only have support for 32 bit wide column counts on 32 bit
                else
                    bufszneed =
                        (int) table_get_column_name32(t.Handle, (IntPtr) columnIndex, b, (IntPtr) b.Capacity);
                //the intptr cast of the long *might* loose the high 32 bits on a 32 bits platform as tight c++ will only have support for 32 bit wide column counts on 32 bit

                if (b.Capacity <= bufszneed)
                    //Capacity is in .net chars, each of size 16 bits, while bufszneed is in bytes. HOWEVER stringbuilder often store common chars using only 8 bits, making precise calculations troublesome and slow
                {
                    //what we know for sure is that the stringbuilder will hold AT LEAST capacity number of bytes. The c++ dll counts in bytes, so there is always room enough.
                    b.Capacity = bufszneed + 1;
                    //allocate an array that is at least as large as what is needed, plus an extra 0 terminator.
                }
                else
                    loop = false;
            } while (loop);
            return b.ToString();
            //in c# this does NOT result in a copy, we get a string that points to the B buffer (If the now immutable string inside b is reused , it will get itself a new buffer
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_column_name",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr tableView_get_column_name64(IntPtr handle, IntPtr columnIndex,
                                                                 [MarshalAs(UnmanagedType.LPStr)] StringBuilder name,
                                                                 IntPtr bufsize);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_column_name",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr tableView_get_column_name32(IntPtr handle, IntPtr columnIndex,
                                                                 [MarshalAs(UnmanagedType.LPStr)] StringBuilder name,
                                                                 IntPtr bufsize);


        public static string TableViewGetColumnName(TableView tv, long columnIndex)
            //ColumnIndex not a long bc on the c++ side it might be 32bit long on a 32 bit platform
        {
            var b = new StringBuilder(16);
            //string builder 16 is just a wild guess that most fields are shorter than this
            bool loop = true;
            do
            {
                int bufszneed;
                if (Is64Bit)
                    bufszneed =
                        (int) tableView_get_column_name64(tv.Handle, (IntPtr) columnIndex, b, (IntPtr) b.Capacity);
                    //the intptr cast of the long *might* loose the high 32 bits on a 32 bits platform as tight c++ will only have support for 32 bit wide column counts on 32 bit
                else
                    bufszneed =
                        (int) tableView_get_column_name32(tv.Handle, (IntPtr) columnIndex, b, (IntPtr) b.Capacity);
                //the intptr cast of the long *might* loose the high 32 bits on a 32 bits platform as tight c++ will only have support for 32 bit wide column counts on 32 bit

                if (b.Capacity <= bufszneed)
                    //Capacity is in .net chars, each of size 16 bits - usually encoding one unicode codepoint, sometimes two are used to code one codepoint, while bufszneed is in bytes. 
                {
                    //what we know for sure is that the stringbuilder will hold AT LEAST capacity number of bytes. The c++ dll counts in bytes, so there is always room enough.
                    b.Capacity = bufszneed + 1;
                    //allocate an array that is at least as large as what is needed, plus an extra 0 terminator.
                }
                else
                    loop = false;
            } while (loop);
            return b.ToString();
            //in c# this does NOT result in a copy, we get a string that points to the B buffer (If the now immutable string inside b is reused , it will get itself a new buffer
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_string",CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_string64(IntPtr handle, IntPtr columnIndex,IntPtr rowIndex,
                                                                 [MarshalAs(UnmanagedType.LPStr)] StringBuilder name,
                                                                 IntPtr bufsize);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_string",CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_string32(IntPtr handle, IntPtr columnIndex,IntPtr rowIndex,
                                                                 [MarshalAs(UnmanagedType.LPStr)] StringBuilder name,
                                                                 IntPtr bufsize);


        public static string TableGetString(Table t, long columnIndex,long rowIndex)
            //ColumnIndex not a long bc on the c++ side it might be 32bit long on a 32 bit platform
        {
            var b = new StringBuilder(16);
            //string builder 16 is just a wild guess that most fields are shorter than this
            bool loop = true;
            do
            {
                int bufszneed;
                if (Is64Bit)
                    bufszneed =
                        (int) table_get_string64(t.Handle, (IntPtr) columnIndex,(IntPtr)rowIndex, b, (IntPtr) b.Capacity);
                    //the intptr cast of the long *might* loose the high 32 bits on a 32 bits platform as tight c++ will only have support for 32 bit wide column counts on 32 bit
                else
                    bufszneed =
                        (int) table_get_string32(t.Handle, (IntPtr) columnIndex,(IntPtr)rowIndex, b, (IntPtr) b.Capacity);
                //the intptr cast of the long *might* loose the high 32 bits on a 32 bits platform as tight c++ will only have support for 32 bit wide column counts on 32 bit

                if (b.Capacity <= bufszneed)
                    //Capacity is in .net chars, each of size 16 bits - usually encoding one unicode codepoint, sometimes two are used to code one codepoint, while bufszneed is in bytes. 
                {
                    //what we know for sure is that the stringbuilder will hold AT LEAST capacity number of bytes. The c++ dll counts in bytes, so there is always room enough.
                    b.Capacity = bufszneed + 1;
                    //allocate an array that is at least as large as what is needed, plus an extra 0 terminator.
                }
                else
                    loop = false;
            } while (loop);
            return b.ToString();
            //in c# this does NOT result in a copy, we get a string that points to the B buffer (If the now immutable string inside b is reused , it will get itself a new buffer
        }




        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableview_get_string64(IntPtr handle, IntPtr columnIndex, IntPtr rowIndex,
                                                                 [MarshalAs(UnmanagedType.LPStr)] StringBuilder name,
                                                                 IntPtr bufsize);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableview_get_string32(IntPtr handle, IntPtr columnIndex, IntPtr rowIndex,
                                                                 [MarshalAs(UnmanagedType.LPStr)] StringBuilder name,
                                                                 IntPtr bufsize);


        public static string TableviewGetString(TableView tv, long columnIndex, long rowIndex)
        //ColumnIndex not a long bc on the c++ side it might be 32bit long on a 32 bit platform
        {
            var b = new StringBuilder(16);
            //string builder 16 is just a wild guess that most fields are shorter than this
            bool loop = true;
            do
            {
                int bufszneed;
                if (Is64Bit)
                    bufszneed =
                        (int)tableview_get_string64(tv.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, b, (IntPtr)b.Capacity);
                //the intptr cast of the long *might* loose the high 32 bits on a 32 bits platform as tight c++ will only have support for 32 bit wide column counts on 32 bit
                else
                    bufszneed =
                        (int)tableview_get_string32(tv.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, b, (IntPtr)b.Capacity);
                //the intptr cast of the long *might* loose the high 32 bits on a 32 bits platform as tight c++ will only have support for 32 bit wide column counts on 32 bit

                if (b.Capacity <= bufszneed)
                //Capacity is in .net chars, each of size 16 bits - usually encoding one unicode codepoint, sometimes two are used to code one codepoint, while bufszneed is in bytes. 
                {
                    //what we know for sure is that the stringbuilder will hold AT LEAST capacity number of bytes. The c++ dll counts in bytes, so there is always room enough.
                    b.Capacity = bufszneed + 1;
                    //allocate an array that is at least as large as what is needed, plus an extra 0 terminator.
                }
                else
                    loop = false;
            } while (loop);
            return b.ToString();
            //in c# this does NOT result in a copy, we get a string that points to the B buffer (If the now immutable string inside b is reused , it will get itself a new buffer
        }















        [DllImport("tightdb_c_cs64", EntryPoint = "spec_get_column_name", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr spec_get_column_name64(IntPtr specHandle, IntPtr columnIndex,
                                                            [MarshalAs(UnmanagedType.LPStr)] StringBuilder name,
                                                            IntPtr bufsize);

        [DllImport("tightdb_c_cs32", EntryPoint = "spec_get_column_name", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr spec_get_column_name32(IntPtr specHandle, IntPtr columnIndex,
                                                            [MarshalAs(UnmanagedType.LPStr)] StringBuilder name,
                                                            IntPtr bufsize);


        public static String SpecGetColumnName(Spec spec, long columnIndex)
        {
            //see table_get_column_name for comments
            var b = new StringBuilder(16);
            bool loop = true;
            do
            {
                int bufszneed;
                if (Is64Bit)
                    bufszneed =
                        (int) spec_get_column_name64(spec.Handle, (IntPtr) columnIndex, b, (IntPtr) b.Capacity);
                else
                    bufszneed =
                        (int) spec_get_column_name32(spec.Handle, (IntPtr) columnIndex, b, (IntPtr) b.Capacity);


                if (b.Capacity <= bufszneed)
                {
                    b.Capacity = bufszneed + 1;
                }
                else
                    loop = false;
            } while (loop);

            return b.ToString();
        }




        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_column_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_column_type64(IntPtr tablePtr, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_column_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_column_type32(IntPtr tablePtr, IntPtr columnIndex);

        public static DataType TableGetColumnType(Table t, long columnIndex)
        {
            if (Is64Bit)
                return IntPtrToDataType(table_get_column_type64(t.Handle, (IntPtr)columnIndex));
            return IntPtrToDataType(table_get_column_type32(t.Handle, (IntPtr)columnIndex));
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_column_type",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_get_column_type64(IntPtr tableViewPtr, IntPtr columnIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_column_type",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_get_column_type32(IntPtr tableViewPtr, IntPtr columnIndex);

        public static DataType TableViewGetColumnType(TableView tv, long columnIndex)
        {
            if (Is64Bit)
                return IntPtrToDataType(tableView_get_column_type64(tv.Handle, (IntPtr)columnIndex));
            return IntPtrToDataType(tableView_get_column_type32(tv.Handle, (IntPtr)columnIndex));
        }



        //we have to trust that c++ DataType fills up the same amount of stack space as one of our own DataType enum's
        //This is the case on windows, visual studio2010 and 2012 but Who knows if some c++ compiler somewhere someday decides to store DataType differently       
        //
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_mixed_type",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_get_mixed_type64(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_mixed_type",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_get_mixed_type32(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex);

        public static DataType TableViewGetMixedType(TableView t, long columnIndex, long rowIndex)
        {
            if (Is64Bit)
                return IntPtrToDataType(tableView_get_mixed_type64(t.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex));
            return IntPtrToDataType(tableView_get_mixed_type32(t.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex));
        }


        //we have to trust that c++ DataType fills up the same amount of stack space as one of our own DataType enum's
        //This is the case on windows, visual studio2010 and 2012 but Who knows if some c++ compiler somewhere someday decides to store DataType differently
        
        //
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_mixed_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_mixed_type64(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_mixed_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_mixed_type32(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex);

        public static DataType TableGetMixedType(Table t, long columnIndex, long rowIndex)
        {
            
            if (Is64Bit)
                return IntPtrToDataType(table_get_mixed_type64(t.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex));
            return IntPtrToDataType( table_get_mixed_type32(t.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex));
           
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "table_update_from_spec", CallingConvention = CallingConvention.Cdecl)
        ]
        private static extern void table_update_from_spec64(IntPtr tablePtr);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_update_from_spec", CallingConvention = CallingConvention.Cdecl)
        ]
        private static extern void table_update_from_spec32(IntPtr tablePtr);

        public static void TableUpdateFromSpec(Table table)
        {
            if (Is64Bit)
                table_update_from_spec64(table.Handle);
            else
                table_update_from_spec32(table.Handle);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_insert_empty_row", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_insert_empty_row64(IntPtr tablePtr, IntPtr rowIndex,IntPtr numRows);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_insert_empty_row", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_insert_empty_row32(IntPtr tablePtr, IntPtr rowIndex, IntPtr numRows);

        public static void TableInsertEmptyRow(Table table, long rowIndex,long numberOfRows)
        {
            if (Is64Bit)
                table_insert_empty_row64(table.Handle, (IntPtr) rowIndex, (IntPtr) numberOfRows);
            else
                table_insert_empty_row32(table.Handle, (IntPtr) rowIndex, (IntPtr) numberOfRows);
        }



        //TIGHTDB_C_CS_API size_t table_add_empty_row(Table* TablePtr, size_t num_rows)

        [DllImport("tightdb_c_cs64", EntryPoint = "table_add_empty_row", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_add_empty_row64(IntPtr tablePtr, IntPtr numRows);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_add_empty_row", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_add_empty_row32(IntPtr tablePtr, IntPtr numRows);

        public static long TableAddEmptyRow(Table table, long numberOfRows)
        {
            if (Is64Bit)
                return (long) table_add_empty_row64(table.Handle, (IntPtr) numberOfRows);
            return (long) table_add_empty_row32(table.Handle, (IntPtr) numberOfRows);
        }


        //        TIGHTDB_C_CS_API void table_set_int(Table* TablePtr, size_t column_ndx, size_t row_ndx, int64_t value)
        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_int64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx, long value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_int32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx, long value);

        public static void TableSetLong(Table table, long columnIndex, long rowIndex, long value)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(Table,Column,Row,Value)", table, columnIndex, rowIndex, value);
#endif

            if (Is64Bit)
                table_set_int64(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
            else
                table_set_int32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
        }


        //        TIGHTDB_C_CS_API void table_set_int(Table* TablePtr, size_t column_ndx, size_t row_ndx, int64_t value)
        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_string64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                      [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_string32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                      [MarshalAs(UnmanagedType.LPStr)] string value);

        public static void TableSetString(Table table, long columnIndex, long rowIndex, string value)
        {
            /*
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(Table,Column,Row,Value)", table, columnIndex, rowIndex, value);
#endif*/

            if (Is64Bit)
                table_set_string64(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
            else
                table_set_string32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "table_set__mixed_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_string64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                      [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_mixed_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_string32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                      [MarshalAs(UnmanagedType.LPStr)] string value);

        public static void TableSetMixedString(Table table, long columnIndex, long rowIndex, string value)
        {

            if (Is64Bit)
                table_set_mixed_string64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
            else
                table_set_mixed_string32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set__mixed_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_mixed_string64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                      [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_mixed_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_mixed_string32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                      [MarshalAs(UnmanagedType.LPStr)] string value);

        public static void TableViewSetMixedString(TableView tableView, long columnIndex, long rowIndex, string value)
        {

            if (Is64Bit)
                tableview_set_mixed_string64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
            else
                tableview_set_mixed_string32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
        }







        //        TIGHTDB_C_CS_API void table_set_int(Table* TablePtr, size_t column_ndx, size_t row_ndx, int64_t value)
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_string64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                          [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_string32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                          [MarshalAs(UnmanagedType.LPStr)] string value);

        public static void TableViewSetString(TableView tableView, long columnIndex, long rowIndex, string value)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(TableView,Column,Row,Value)", tableView, columnIndex, rowIndex,
                value);
#endif

            if (Is64Bit)
                tableView_set_string64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
            else
                tableView_set_string32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
        }





        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_int64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx, long value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_int32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx, long value);

        public static void TableViewSetLong(TableView tableView, long columnIndex, long rowIndex, long value)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(TableView,Column,Row,Value)", tableView, columnIndex, rowIndex,
                value);
#endif

            if (Is64Bit)
                tableView_set_int64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
            else
                tableView_set_int32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
        }





        //TIGHTDB_C_CS_API void table_set_mixed_subtable(tightdb::Table* table_ptr,size_t col_ndx, size_t row_ndx,Table* source);
        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_mixed_subtable",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_subtable64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                              IntPtr sourceTablePtr);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_mixed_subtable",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_subtable32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                              IntPtr sourceTablePtr);

        public static void TableSetMixedSubTable(Table table, long columnIndex, long rowIndex, Table sourceTable)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(Table,Column,Row)", table, columnIndex, rowIndex);
#endif

            if (Is64Bit)
                table_set_mixed_subtable64(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, sourceTable.Handle);
            else
                table_set_mixed_subtable32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, sourceTable.Handle);
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_mixed_subtable",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_mixed_subtable64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx,
                                                                  IntPtr sourceTablePtr);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_mixed_subtable",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_mixed_subtable32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx,
                                                                  IntPtr sourceTablePtr);

        public static void TableViewSetMixedSubTable(TableView tableView, long columnIndex, long rowIndex,
                                                     Table sourceTable)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(TableView,Column,Row)", tableView, columnIndex, rowIndex);
#endif

            if (Is64Bit)
                tableView_set_mixed_subtable64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex,
                                               sourceTable.Handle);
            else
                tableView_set_mixed_subtable32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex,
                                               sourceTable.Handle);
        }




        public static void TableSetMixedBinary(Table table, long columnIndex, long rowIndex, byte[] value)
        {
            throw new NotImplementedException("UnsafeNativeMethods.cs TableSetMixedBinary not implemented yet");
        }

        public static void TableViewSetMixedBinary(TableView tableView, long columnIndex, long rowIndex, byte[] value)
        {
            throw new NotImplementedException("UnsafeNativeMethods.cs TableViewSetMixedBinary not implemented yet");
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_mixed_empty_subtable",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_empty_subtable64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_mixed_empty_subtable",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_empty_subtable32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        public static void TableSetMixedEmptySubTable(Table table, long columnIndex, long rowIndex)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(Table,Column,Row)", table, columnIndex, rowIndex);
#endif

            if (Is64Bit)
                table_set_mixed_empty_subtable64(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
            else
                table_set_mixed_empty_subtable32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_mixed_empty_subtable",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_mixed_empty_subtable64(IntPtr tableViewPtr, IntPtr columnNdx,
                                                                        IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_mixed_empty_subtable",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_mixed_empty_subtable32(IntPtr tableViewPtr, IntPtr columnNdx,
                                                                        IntPtr rowNdx);

        public static void TableViewSetMixedEmptySubTable(TableView tableView, long columnIndex, long rowIndex)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(Tableview,Column,Row)", tableView, columnIndex, rowIndex);
#endif

            if (Is64Bit)
                tableView_set_mixed_empty_subtable64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
            else
                tableView_set_mixed_empty_subtable32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
        }







        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_mixed_int", CallingConvention = CallingConvention.Cdecl
            )]
        private static extern void tableView_set_mixed_int64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx,
                                                             long value);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_mixed_int", CallingConvention = CallingConvention.Cdecl
            )]
        private static extern void tableView_set_mixed_int32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx,
                                                             long value);

        public static void TableViewSetMixedLong(TableView tableView, long columnIndex, long rowIndex, long value)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(Tableview,Column,Row,Value)", tableView, columnIndex, rowIndex,
                value);
#endif

            if (Is64Bit)
                tableView_set_mixed_int64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
            else
                tableView_set_mixed_int32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_mixed_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_int64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx, long value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_mixed_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_int32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx, long value);

        public static void TableSetMixedLong(Table table, long columnIndex, long rowIndex, long value)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(Table,Column,Row,Value)", table, columnIndex, rowIndex, value);
#endif

            if (Is64Bit)
                table_set_mixed_int64(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
            else
                table_set_mixed_int32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
        }



        //        TIGHTDB_C_CS_API void insert_int(Table* TablePtr, size_t column_ndx, size_t row_ndx, int64_t value)
        [DllImport("tightdb_c_cs64", EntryPoint = "table_insert_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_insert_int64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx, long value);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_insert_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_insert_int32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx, long value);

        public static void TableInsertInt(Table table, long columnIndex, long rowIndex, long value)
        {
#if DEBUG
            Log(MethodBase.GetCurrentMethod().Name, "(Table,Column,Row,Value)", table, columnIndex, rowIndex, value);
#endif

            if (Is64Bit)
                table_insert_int64(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
            else
                table_insert_int32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, value);
        }



        //TIGHTDB_C_CS_API int64_t table_get_int(Table* TablePtr, size_t column_ndx, size_t row_ndx)
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_get_int64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_get_int32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);


        //note even though it's called getint - it does return an int64_t which is a 64 bit signed, that is, similar to C# long
        public static long TableGetInt(Table table, long columnIndex, long rowIndex)
        {
            if (Is64Bit) return table_get_int64(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
            return table_get_int32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
        }



        //TIGHTDB_C_CS_API int64_t table_get_int(Table* TablePtr, size_t column_ndx, size_t row_ndx)
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableView_get_int64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long tableView_get_int32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);


        public static long TableViewGetInt(TableView tableView, long columnIndex, long rowIndex)
        {
            return Is64Bit
                       ? tableView_get_int64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex)
                       : tableView_get_int32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
        }



        //get an int from a mixed
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_mixed_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_get_mixed_int64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_mixed_int", CallingConvention = CallingConvention.Cdecl)]
        private static extern long table_get_mixed_int32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);


        //note even though it's called getint - it does return an int64_t which is a 64 bit signed, that is, similar to C# long
        public static long TableGetMixedInt(Table table, long columnIndex, long rowIndex)
        {
            if (Is64Bit)
                return table_get_mixed_int64(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
            return table_get_mixed_int32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_mixed_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_mixed_bool64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_mixed_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_mixed_bool32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        //convert.tobool does not take an IntPtr so we have to convert ourselves we get 1 for true, 0 for false
        public static bool TableGetMixedBool(Table table, long columnIndex, long rowIndex)
        {
            if (Is64Bit)
            {
                return IntPtrToBool(table_get_mixed_bool64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex));
            }
            return IntPtrToBool(table_get_mixed_bool32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex));
        }







        //get an int from a mixed
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_mixed_int", CallingConvention = CallingConvention.Cdecl
            )]
        private static extern long tableView_get_mixed_int64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_mixed_int", CallingConvention = CallingConvention.Cdecl
            )]
        private static extern long tableView_get_mixed_int32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);


        //note even though it's called getint - it does return an int64_t which is a 64 bit signed, that is, similar to C# long
        public static long TableViewGetMixedInt(TableView tableView, long columnIndex, long rowIndex)
        {
            return Is64Bit
                       ? tableView_get_mixed_int64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex)
                       : tableView_get_mixed_int32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_mixed_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableview_get_mixed_bool64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_mixed_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableview_get_mixed_bool32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        //convert.tobool does not take an IntPtr so we have to convert ourselves we get 1 for true, 0 for false
        public static bool TableViewGetMixedBool(TableView tableView, long columnIndex, long rowIndex)
        {
            if (Is64Bit)
            {
                return IntPtrToBool(tableview_get_mixed_bool64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex));
            }
            return IntPtrToBool(tableview_get_mixed_bool32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex));
        }






        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern Double table_get_double64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern Double table_get_double32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);


        //note even though it's called getint - it does return an int64_t which is a 64 bit signed, that is, similar to C# long
        public static Double TableGetDouble(Table table, long columnIndex, long rowIndex)
        {
            if (Is64Bit) return table_get_double64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
            return table_get_double32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
        }



        //TIGHTDB_C_CS_API int64_t table_get_int(Table* TablePtr, size_t column_ndx, size_t row_ndx)
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern Double tableView_get_double64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern Double tableView_get_double32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);


        public static Double TableViewGetDouble(TableView tableView, long columnIndex, long rowIndex)
        {
            return Is64Bit
                       ? tableView_get_double64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex)
                       : tableView_get_double32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
        }




        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_get_float64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_get_float32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);


        //note even though it's called getint - it does return an int64_t which is a 64 bit signed, that is, similar to C# long
        public static float TableGetFloat(Table table, long columnIndex, long rowIndex)
        {
            if (Is64Bit) return table_get_float64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
            return table_get_float32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
        }



        //TIGHTDB_C_CS_API int64_t table_get_int(Table* TablePtr, size_t column_ndx, size_t row_ndx)
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableView_get_float64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableView_get_float32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);


        public static float TableViewGetFloat(TableView tableView, long columnIndex, long rowIndex)
        {
            return Is64Bit
                       ? tableView_get_float64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex)
                       : tableView_get_float32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
        }



















        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int64 tableView_get_date64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int64 tableView_get_date32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);
        public static DateTime TableViewGetDateTime(TableView tableView, long columnIndex, long rowIndex)
        {
            Int64 cppdate = Is64Bit
                                ? tableView_get_date64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex)
                                : tableView_get_date32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
            return ToCSharpTimeUtc(cppdate);
        }
                
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int64 table_get_date64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int64 table_get_date32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);
        
        public static DateTime TableGetDateTime(Table table, long columnIndex, long rowIndex)
        {

            Int64 cppdate =
             Is64Bit
                       ? table_get_date64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex)
                       : table_get_date32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
            return ToCSharpTimeUtc(cppdate);
        }

        

        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_mixed_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int64 tableView_get_mixed_date64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_mixed_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int64 tableView_get_mixed_date32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);
        public static DateTime TableViewGetMixedDateTime(TableView tableView, long columnIndex, long rowIndex)
        {
            Int64 cppdate = Is64Bit
                                ? tableView_get_mixed_date64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex)
                                : tableView_get_mixed_date32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex);
            return ToCSharpTimeUtc(cppdate);
        }

        //the call should return a time_t in 64 bit format (always) so we marshal it as long
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_mixed_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int64 table_get_mixed_date64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_mixed_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int64 table_get_mixed_date32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        public static DateTime TableGetMixedDateTime(Table table, long columnIndex, long rowIndex)
        {
            
            Int64 cppdate=
             Is64Bit
                       ?  table_get_mixed_date64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex)
                       : table_get_mixed_date32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
            return ToCSharpTimeUtc(cppdate);
        }



        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_mixed_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern Double table_get_mixed_double64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_mixed_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern Double table_get_mixed_double32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        public static Double TableGetMixedDouble(Table table, long columnIndex, long rowIndex)
        {
            return 
             Is64Bit
                       ? table_get_mixed_double64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex)
                       : table_get_mixed_double32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);            
        }


        //the call should return a time_t in 64 bit format (always) so we marshal it as long
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_mixed_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern Double tableView_get_mixed_double64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_mixed_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern Double tableView_get_mixed_double32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);

        public static Double TableViewGetMixedDouble(TableView tableView, long columnIndex, long rowIndex)
        {
            return
             Is64Bit
                       ? tableView_get_mixed_double64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex)
                       : tableView_get_mixed_double32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);
        }




        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_mixed_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_get_mixed_float64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_mixed_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float table_get_mixed_float32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        public static float TableGetMixedFloat(Table table, long columnIndex, long rowIndex)
        {

            return
             Is64Bit
                       ? table_get_mixed_float64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex)
                       : table_get_mixed_float32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);


        }




        //the call should return a time_t in 64 bit format (always) so we marshal it as long
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_mixed_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableView_get_mixed_float64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_mixed_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern float tableView_get_mixed_float32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);

        public static float TableViewGetMixedFloat(TableView tableView, long columnIndex, long rowIndex)
        {

            return
             Is64Bit
                       ? tableView_get_mixed_float64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex)
                       : tableView_get_mixed_float32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex);

        }




        [DllImport("tightdb_c_cs64", EntryPoint = "table_size", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_size64(IntPtr tablePtr);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_size", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_size32(IntPtr tablePtr);

        public static long TableSize(Table t)
        {
            if (Is64Bit)
                return (long) table_get_size64(t.Handle);
            return (long) table_get_size32(t.Handle);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "table_get_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_bool64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "table_get_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr table_get_bool32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx);

        //convert.tobool does not take an IntPtr so we have to convert ourselves we get 1 for true, 0 for false
        public static bool TableGetBool(Table table, long columnIndex, long rowIndex)
        {
            if (Is64Bit)
            {
                return IntPtrToBool(table_get_bool64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex));
            }
            return IntPtrToBool(table_get_bool32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex));
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_get_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_get_bool64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);

        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_get_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr tableView_get_bool32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx);

        //convert.tobool does not take an IntPtr so we have to convert ourselves we get 1 for true, 0 for false
        public static bool TableViewGetBool(TableView tableView, long columnIndex, long rowIndex)
        {
            if (Is64Bit)
            {
                return IntPtrToBool(tableView_get_bool64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex));
            }
            return IntPtrToBool(tableView_get_bool32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex));
        }



        
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_bool64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx,
                                                        IntPtr value);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_bool32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx,
                                                        IntPtr value);
      
        //convert.tobool does not take an IntPtr so we have to convert ourselves we get 1 for true, 0 for false
        public static void TableViewSetBool(TableView tableView, long columnIndex, long rowIndex, Boolean value)
        {            
            var ipValue = BoolToIntPtr(value);
            if (Is64Bit)
                tableView_set_bool64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, ipValue);
            else
                tableView_set_bool32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, ipValue);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_bool64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                        IntPtr value);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_bool32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                        IntPtr value);

        //convert.tobool does not take an IntPtr so we have to convert ourselves we get 1 for true, 0 for false
        public static void TableSetBool(Table table, long columnIndex, long rowIndex, Boolean value)
        {
            IntPtr ipValue = BoolToIntPtr(value);

            if (Is64Bit)
                table_set_bool64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ipValue);
            else
                table_set_bool32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ipValue);
        }





        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_mixed_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_mixed_bool64(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx,
                                                        IntPtr value);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_mixed_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_mixed_bool32(IntPtr tableViewPtr, IntPtr columnNdx, IntPtr rowNdx,
                                                        IntPtr value);

        //convert.tobool does not take an IntPtr so we have to convert ourselves we get 1 for true, 0 for false
        public static void TableViewSetMixedBool(TableView tableView, long columnIndex, long rowIndex, Boolean value)
        {
            var ipValue = BoolToIntPtr(value);
            if (Is64Bit)
                tableview_set_mixed_bool64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ipValue);
            else
                tableview_set_mixed_bool32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ipValue);
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_mixed_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_bool64(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                        IntPtr value);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_mixed_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_bool32(IntPtr tablePtr, IntPtr columnNdx, IntPtr rowNdx,
                                                        IntPtr value);

        //convert.tobool does not take an IntPtr so we have to convert ourselves we get 1 for true, 0 for false
        public static void TableSetMixedBool(Table table, long columnIndex, long rowIndex, Boolean value)
        {
            IntPtr ipValue = BoolToIntPtr(value);

            if (Is64Bit)
                table_set_mixed_bool64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ipValue);
            else
                table_set_mixed_bool32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ipValue);
        }













        
        [DllImport("tightdb_c_cs64", EntryPoint = "query_get_column_index", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr query_get_column_index64(IntPtr queryPtr, String columnName);
        [DllImport("tightdb_c_cs32", EntryPoint = "query_get_column_index", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr query_get_column_index32(IntPtr queryPtr, String columnName);

        public static long QueryGetColumnIndex(Query q, String columnName)
        {
            if(Is64Bit)
                return (long) query_get_column_index64(q.Handle,columnName);
            return (long) query_get_column_index32(q.Handle, columnName);
        }



        //in tightdb c++ this function returns q again, the query object is re-used and keeps its pointer.
        //so high-level stuff should also return self, to enable stacking of operations query.dothis().dothat()
        [DllImport("tightdb_c_cs64", EntryPoint = "query_bool_equal", CallingConvention = CallingConvention.Cdecl)]
        private static extern void query_bool_equal64(IntPtr queryPtr, IntPtr columnIndex,IntPtr value);
        [DllImport("tightdb_c_cs32", EntryPoint = "query_bool_equal", CallingConvention = CallingConvention.Cdecl)]
        private static extern void query_bool_equal32(IntPtr queryPtr, IntPtr columnIndex, IntPtr value);
        public static void QueryBoolEqual(Query q,long columnIndex, bool value)
        {
            var ipValue = BoolToIntPtr(value);
            if (Is64Bit)
                query_bool_equal64(q.Handle, (IntPtr)columnIndex, ipValue);
            else 
                 query_bool_equal32(q.Handle, (IntPtr)columnIndex, ipValue);  
        }



        //in tightdb c++ this function returns q again, the query object is re-used and keeps its pointer.
        //so high-level stuff should also return self, to enable stacking of operations query.dothis().dothat()
        [DllImport("tightdb_c_cs64", EntryPoint = "query_int_between", CallingConvention = CallingConvention.Cdecl)]
        private static extern void query_int_between64(IntPtr queryPtr, IntPtr columnIndex, long lowValue, long highValue);
        [DllImport("tightdb_c_cs32", EntryPoint = "query_int_between", CallingConvention = CallingConvention.Cdecl)]
        private static extern void query_int_between32(IntPtr queryPtr, IntPtr columnIndex, long lowValue, long highValue);
        public static void QueryIntBetween(Query q, long columnIndex, long lowValue, long highValue)
        {
            if (Is64Bit)
                query_int_between64(q.Handle, (IntPtr)columnIndex, lowValue, highValue);
            else
                query_int_between32(q.Handle, (IntPtr)columnIndex, lowValue, highValue);
        }





       
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_mixed_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_mixed_double64(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, double value);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_mixed_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_mixed_double32(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, double value);

        public static void TableViewSetMixedDouble(TableView tableView, long columnIndex, long rowIndex, double value)
        {
            if (Is64Bit)
                tableview_set_mixed_double64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
            else
            tableview_set_mixed_double32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
        }

        
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_double64(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, double value);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_double32(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, double value);

        public static void TableViewSetDouble(TableView tableView, long columnIndex, long rowIndex, double value)
        {
            if (Is64Bit)
                tableview_set_double64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
            else
            tableview_set_double32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
        }


        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_mixed_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_double64(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, double value);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_mixed_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_double32(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, double value);

        public static void TableSetMixedDouble(Table table, long columnIndex, long rowIndex, double value)
        {
            if (Is64Bit)
                table_set_mixed_double64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
            else

            table_set_mixed_double32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
        }

        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_double64(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, double value);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_double32(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, double value);

        public static void TableSetDouble(Table table, long columnIndex, long rowIndex, double value)
        {
            if (Is64Bit)
                table_set_double64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
            else
            table_set_double32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
        }







        
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_mixed_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_mixed_float64(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, float value);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_mixed_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_mixed_float32(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, float value);

        public static void TableViewSetMixedFloat(TableView tableView, long columnIndex, long rowIndex, float value)
        {
            //Console.WriteLine("Tableviewsetmixedfloat calling with {0}",value);
            if (Is64Bit)
                tableview_set_mixed_float64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
            else
            tableview_set_mixed_float32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
        }


        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_mixed_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_float64(IntPtr tablePtr, IntPtr columnIndex,IntPtr rowIndex, float value);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_mixed_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_float32(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, float value);

        public static void TableSetMixedFloat(Table table ,long columnIndex, long rowIndex, float value)
        {
            //Console.WriteLine("TableSetMixedFloat calling with {0}", value);

            if (Is64Bit)
                table_set_mixed_float64(table.Handle, (IntPtr)columnIndex,(IntPtr) rowIndex, value);
            else
            table_set_mixed_float32(table.Handle, (IntPtr)columnIndex, (IntPtr) rowIndex, value);            
        }


        
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_float64(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, float value);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_float32(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, float value);

        public static void TableViewSetFloat(TableView tableView, long columnIndex, long rowIndex, float value)
        {
            if (Is64Bit)
                tableview_set_float64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
            else
            tableview_set_float32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
        }


        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_float64(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, float value);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_float", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_float32(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, float value);

        public static void TableSetFloat(Table table, long columnIndex, long rowIndex, float value)
        {
            if (Is64Bit)
                table_set_float64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
            else
            table_set_float32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, value);
        }


        //setting and getting Date fields from the database

        //keeping this static might speed things up, instead of instantiating a new one every time
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0,DateTimeKind.Utc);

        //convert a DateTime to a 64 bit integer to be marshalled to a time_t on the other side
        //NOTE THAT TIGHTDB ROUNDS DOWN TO NEAREST SECOND WHEN STORING A DATETIME
        //Note also that the date supplied is converted to UTC - we assume that the user has set the datetimekind.utc if it is already        
        //ALSO NOTE THAT TIGHTDB CANNOT STORE time_t values that are negative, effectively tighdb is not able to store dates before 1970,1,1
        public static Int64 ToTightDbTime(DateTime date)
        {            
            return (Int64)(date.ToUniversalTime() - Epoch).TotalSeconds;
        }

        public static Int64 ToTightDbMixedTime(DateTime date)
        {
            Int64 retval = ToTightDbTime(date);
            if (retval < 0)
            {
                throw new ArgumentOutOfRangeException("date", "The date specified is not a valid tightdb mixed date. Tightdb mixed dates must be positive time_t dates, from jan.1.1970 and onwards ");
            }
            return retval;
        }

        //not used
        //CppTime is expected to be a time_t (UTC since 1970,1,1). While currently tightdb cannot handle negative time_t, this method will work wiht those just fine
        /*
        public static DateTime ToCSharpTimeLocalTime(Int64 cppTime)
        {            
            return  ToCSharpTimeUtc(cppTime).ToLocalTime();
        }
        */


        //CppTime is expected to be a time_t (UTC since 1970,1,1 measured in seconds)
        public static DateTime ToCSharpTimeUtc(Int64 cppTime)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Convert.ToDouble(cppTime));//unfortunate that addseconds takes a double. Addseconds rounds the double is rounded to nearest millisecond so we should not have problems with loose precision
        }

        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_mixed_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_date64(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, Int64 value);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_mixed_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_mixed_date32(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, Int64 value);

        public static void TableSetMixedDate(Table table, long columnIndex, long rowIndex, DateTime value)
        {
            if (Is64Bit)
                table_set_mixed_date64(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, ToTightDbMixedTime(value));
            else
                table_set_mixed_date32(table.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex, ToTightDbMixedTime(value));
        }

        
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_mixed_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_mixed_date64(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, Int64 value);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_mixed_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableView_set_mixed_date32(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, Int64 value);

        public static void TableViewSetMixedDate(TableView tableView, long columnIndex, long rowIndex, DateTime value)
        {
            if (Is64Bit)
                tableView_set_mixed_date64(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex,
                                           ToTightDbMixedTime(value));
            else
                tableView_set_mixed_date32(tableView.Handle, (IntPtr) columnIndex, (IntPtr) rowIndex,
                                           ToTightDbMixedTime(value));
        }


        
        [DllImport("tightdb_c_cs64", EntryPoint = "table_set_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_date64(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, Int64 value);
        [DllImport("tightdb_c_cs32", EntryPoint = "table_set_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern void table_set_date32(IntPtr tablePtr, IntPtr columnIndex, IntPtr rowIndex, Int64 value);

        public static void TableSetDate(Table table, long columnIndex, long rowIndex, DateTime value)
        {
            if (Is64Bit)
                table_set_date64(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ToTightDbTime(value));
            else
            table_set_date32(table.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ToTightDbTime(value));
        }

        
        [DllImport("tightdb_c_cs64", EntryPoint = "tableview_set_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_date64(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, Int64 value);
        [DllImport("tightdb_c_cs32", EntryPoint = "tableview_set_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern void tableview_set_date32(IntPtr tableViewPtr, IntPtr columnIndex, IntPtr rowIndex, Int64 value);

        public static void TableViewSetDate(TableView tableView, long columnIndex, long rowIndex, DateTime value)
        {
            if (Is64Bit)
                tableview_set_date64(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ToTightDbTime(value));
            else
            tableview_set_date32(tableView.Handle, (IntPtr)columnIndex, (IntPtr)rowIndex, ToTightDbTime(value));
        }


        




        //these methods are meant to be used for testing that the c++ library passes types on the stack of size, sequence and logical representation 
        //expected by the C# end.
        //the methods below are not used except in the test part of initialization
        //it looks like we are testing stuff that is defined to be set in stone, but especially the C# end could theoretically change, and it is not known until
        //we are running inside some CLR implementation. We also do not know the workings of the marshalling layer of the CLI we are running on, or the marshalling
        //layer of the C# compiler that built the C# source. Reg. the C++ compiler that built the c++ binding, we don't know for sure if it has bugs or quirks.
        //Because a problem would be extremely hard to track down, we make sure things work as we expect them to
        //if this method does not throw an exception, the types we use to communicate to/from c++ are safe and work as expected
        [DllImport("tightdb_c_cs64", EntryPoint = "test_sizeofsize_t", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int32 test_sizeofsize_t64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_sizeofsize_t", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int32 test_sizeofsize_t32();

        public static Int32 TestSizeOfSizeT()
        {
            if (Is64Bit)
                return test_sizeofsize_t64();
            return test_sizeofsize_t32();
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_sizeofint32_t", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeofint32_t64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_sizeofint32_t", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeofint32_t32();

        public static IntPtr TestSizeOfInt32_T()
        {
            if (Is64Bit)
                return test_sizeofint32_t64();
            return test_sizeofint32_t32();
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_sizeoftablepointer", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeoftablepointer64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_sizeoftablepointer", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeoftablepointer32();

        public static IntPtr TestSizeOfTablePointer()
        {
            if (Is64Bit)
                return test_sizeoftablepointer64();
            return test_sizeoftablepointer32();
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_sizeofcharpointer", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeofcharpointer64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_sizeofcharpointer", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeofcharpointer32();

        public static IntPtr TestSizeOfCharPointer()
        {
            if (Is64Bit)
                return test_sizeofcharpointer64();
            return test_sizeofcharpointer32();
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_sizeofint64_t", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeofint64_t64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_sizeofint64_t", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeofint64_t32();

        public static IntPtr Testsizeofint64_t()
        {
            if (Is64Bit)
                return test_sizeofint64_t64();
            return test_sizeofint64_t32();
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "test_sizeoffloat", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeoffloat64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_sizeoffloat", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeoffloat32();

        public static IntPtr TestSizeOfFloat()
        {
            if (Is64Bit)
                return test_sizeoffloat64();
            return test_sizeoffloat32();
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "test_sizeofdouble", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeofdouble64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_sizeofdouble", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeofdouble32();

        public static IntPtr TestSizeOfDouble()
        {
            if (Is64Bit)
                return test_sizeofdouble64();
            return test_sizeofdouble32();
        }

        


        [DllImport("tightdb_c_cs64", EntryPoint = "test_sizeoftime_t", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeoftime_t64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_sizeoftime_t", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_sizeoftime_t32();

        public static IntPtr TestSizeOfTime_T()
        {
            if (Is64Bit)
                return test_sizeoftime_t64();
            return test_sizeoftime_t32();
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "test_get_five_parametres", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_get_five_parametres64(IntPtr p1, IntPtr p2,IntPtr p3, IntPtr p4, IntPtr p5);
        [DllImport("tightdb_c_cs32", EntryPoint = "test_get_five_parametres", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_get_five_parametres32(IntPtr p1, IntPtr p2, IntPtr p3, IntPtr p4, IntPtr p5);

        public static IntPtr TestGetFiveParametres(IntPtr p1, IntPtr p2, IntPtr p3, IntPtr p4, IntPtr p5)
        {
            if (Is64Bit)
                return test_get_five_parametres64(p1, p2, p3, p4, p5);
            return test_get_five_parametres32(p1, p2, p3, p4, p5);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "test_float_max", CallingConvention = CallingConvention.Cdecl)]
        private static extern float test_float_max64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_float_max", CallingConvention = CallingConvention.Cdecl)]
        private static extern float test_float_max32();

        public static float TestFloatMax()
        {
            if (Is64Bit)
                return test_float_max64();
            return test_float_max32();
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_float_min", CallingConvention = CallingConvention.Cdecl)]
        private static extern float test_float_min64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_float_min", CallingConvention = CallingConvention.Cdecl)]
        private static extern float test_float_min32();

        public static float TestFloatMin()
        {
            if (Is64Bit)
                return test_float_min64();
            return test_float_min32();
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "test_float_return", CallingConvention = CallingConvention.Cdecl)]
        private static extern float test_float_return64(float value);
        [DllImport("tightdb_c_cs32", EntryPoint = "test_float_return", CallingConvention = CallingConvention.Cdecl)]
        private static extern float test_float_return32(float value);

        public static float TestFloatReturn(float value)
        {
            if (Is64Bit)
                return test_float_return64(value);
            return test_float_return32(value);
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "test_double_max", CallingConvention = CallingConvention.Cdecl)]
        private static extern double test_double_max64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_double_max", CallingConvention = CallingConvention.Cdecl)]
        private static extern double test_double_max32();

        public static double TestDoubleMax()
        {
            if (Is64Bit)
                return test_double_max64();
            return test_double_max32();
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_double_min", CallingConvention = CallingConvention.Cdecl)]
        private static extern double test_double_min64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_double_min", CallingConvention = CallingConvention.Cdecl)]
        private static extern double test_double_min32();

        public static double TestDoubleMin()
        {
            if (Is64Bit)
                return test_double_min64();
            return test_double_min32();
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "test_double_return", CallingConvention = CallingConvention.Cdecl)]
        private static extern double test_double_return64(double value);
        [DllImport("tightdb_c_cs32", EntryPoint = "test_double_return", CallingConvention = CallingConvention.Cdecl)]
        private static extern double test_double_return32(double value);

        public static double TestDoubleReturn(double value)
        {
            if (Is64Bit)
                return test_double_return64(value);
            return test_double_return32(value);
        }





        [DllImport("tightdb_c_cs64", EntryPoint = "test_int64_t_max", CallingConvention = CallingConvention.Cdecl)]
        private static extern long test_int64_t_max64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_int64_t_max", CallingConvention = CallingConvention.Cdecl)]
        private static extern long test_int64_t_max32();

        public static long TestLongMax()
        {
            if (Is64Bit)
                return test_int64_t_max64();
            return test_int64_t_max32();
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_int64_t_min", CallingConvention = CallingConvention.Cdecl)]
        private static extern long test_int64_t_min64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_int64_t_min", CallingConvention = CallingConvention.Cdecl)]
        private static extern long test_int64_t_min32();

        public static long TestLongMin()
        {
            if (Is64Bit)
                return test_int64_t_min64();
            return test_int64_t_min32();
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "test_int64_t_return", CallingConvention = CallingConvention.Cdecl)]
        private static extern long test_int64_t_return64(long value);
        [DllImport("tightdb_c_cs32", EntryPoint = "test_int64_t_return", CallingConvention = CallingConvention.Cdecl)]
        private static extern long test_int64_t_return32(long value);

        public static long TestLongReturn(long value)
        {
            if (Is64Bit)
                return test_int64_t_return64(value);
            return test_int64_t_return32(value);
        }








        [DllImport("tightdb_c_cs64", EntryPoint = "test_size_t_max", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_size_t_max64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_size_t_max", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_size_t_max32();

        public static IntPtr TestSizeTMax()
        {
            if (Is64Bit)
                return test_size_t_max64();
            return test_size_t_max32();
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_size_t_min", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_size_t_min64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_size_t_min", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_size_t_min32();

        public static IntPtr TestSizeTMin()
        {
            if (Is64Bit)
                return test_size_t_min64();
            return test_size_t_min32();
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "test_size_t_return", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_size_t_return64(IntPtr value);
        [DllImport("tightdb_c_cs32", EntryPoint = "test_size_t_return", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_size_t_return32(IntPtr value);

        public static IntPtr TestSizeTReturn(IntPtr value)
        {
            if (Is64Bit)
                return test_size_t_return64(value);
            return test_size_t_return32(value);
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "test_return_datatype", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_return_datatype64(IntPtr value);
        [DllImport("tightdb_c_cs32", EntryPoint = "test_return_datatype", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_return_datatype32(IntPtr value);

        public static DataType TestReturnDataType(DataType value)
        {
            if (Is64Bit)
                return IntPtrToDataType(test_return_datatype64(DataTypeToIntPtr(value)));
            return IntPtrToDataType(test_return_datatype32(DataTypeToIntPtr(value)));
        }


        [DllImport("tightdb_c_cs64", EntryPoint = "test_return_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_return_bool64(IntPtr value);
        [DllImport("tightdb_c_cs32", EntryPoint = "test_return_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_return_bool32(IntPtr value);

        public static Boolean TestReturnBoolean(Boolean value)
        {
            if (Is64Bit)
                return IntPtrToBool(test_return_bool64(BoolToIntPtr(value)));
            return IntPtrToBool(test_return_bool32(BoolToIntPtr(value)));
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_return_true_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_return_true_bool64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_return_true_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_return_true_bool32();

        public static Boolean TestReturnTrueBool()
        {
            if (Is64Bit)
                return IntPtrToBool(test_return_true_bool64());
            return IntPtrToBool(test_return_true_bool32());
        }



        [DllImport("tightdb_c_cs64", EntryPoint = "test_return_false_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_return_false_bool64();
        [DllImport("tightdb_c_cs32", EntryPoint = "test_return_false_bool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr test_return_false_bool32();

        public static Boolean TestReturnFalseBool()
        {
            if (Is64Bit)
                return IntPtrToBool(test_return_false_bool64());
            return IntPtrToBool(test_return_false_bool32());
        }

        [DllImport("tightdb_c_cs64", EntryPoint = "test_increment_integer", CallingConvention = CallingConvention.Cdecl)]
        private static extern long test_increment_integer64(long value);
        [DllImport("tightdb_c_cs32", EntryPoint = "test_increment_integer", CallingConvention = CallingConvention.Cdecl)]
        private static extern long test_increment_integer32(long value);

        public static long TestIncrementInteger(long value)
        {
            if (Is64Bit)
                return test_increment_integer64(value);
            return test_increment_integer32(value);
        }



        //this can be called when initializing, and it will throw an exception if anything is wrong with the type binding between the  C#
        //program running right now, and the c++ library that has been loaded.
        //The test should reveal if any of the types we use are marshalled wrongly, especially
        //size on the stack
        //min and max values match
        //parametres are sent in the right sequence
        //logical meaning of the basic types we use

        //for instance this test would discover if a time_t on the c++ side is 32 bit, while the C# side expects 64 bits.
        //The test will be valuable if any of these things change :
        //C# compiler, CLR implementation, c++ compiler that built the c++ part, operating system
        
        //the current code is expected to work on these CLR (Common Language Runtime)implementations :
        //windows 32 bit
        //windows 64 bit
        //support for these are partial (some unit tests still fail)
        //mono windows 32 bit
        //mono windows 64 bit
        //support for these are coming up (stil untested, some c++ work needed)
        //mono linux x86    (selected distributions)
        //mono linux x86-64 (selected distributions)

        //the current C# code is expected to work on these C# compilers
        //windows C# target x64
        //windows C# target x32
        //windows C# target anycpu
        //support for these are coming up
        //mono C# compiler target x64
        //mono C# compiler target x32
        //mono C# compiler target AnyCpu
        //mono C# compiler other targets (SPARC, PowerPC,S390 64 bit... )
        
        //the current C# and c++ code is expected and tested to work on these operating systems (system platforms)
        //windows 7 32 bit (using .net, compiled with VS2012 C# and VS2012 c++)
        //windows 7 64 bit (using .net, compiled with VS2012 C# and VS2012 c++)
        
        //the following platforms will be supported when needed
        //windows phone 
        //silverlight 
        //ps3 (using mono/linux)
        //android (using mono/linux)
        
        //there are many more combinations. To enable a combination to work, the following must be done
        //1) ensure tightdb and the c++ part of the binding can be built on the platform
        //2) ensure there is a CLR on the platform
        //3) any needed changes to the C# part of the binding must be implemented (could be calling conventions, type sizes, quirks with marshalling, names of library files to load, etc)
        //4) optional - ensure that the platform C# compiler can build the C# part of tightdb 
        //  (if the C# platform is not binary compatible with .net we have to build on the specific C# platform, if it is binary compatible this is optional)


        public static void TestInteropSizes()
        {
            int sizeOfIntPtr = IntPtr.Size;
            int sizeOfSizeT = TestSizeOfSizeT();
            if (sizeOfIntPtr != sizeOfSizeT)
            {
                throw new TableException(String.Format(CultureInfo.InvariantCulture, "The size_t size{0} does not match the size of UintPtr{1}", sizeOfSizeT, sizeOfIntPtr));
            }

            IntPtr sizeOfInt32T = TestSizeOfInt32_T();
            var sizeOfInt32 = (IntPtr)sizeof(Int32);
            if (sizeOfInt32T != sizeOfInt32)
            {
                throw new TableException(String.Format(CultureInfo.InvariantCulture, "The int32_t size{0} does not match the size of Int32{1}", sizeOfInt32T, sizeOfInt32));
            }
            //from here on, we know that size_t maps fine to IntPtr, and Int32_t maps fine to Int32

            IntPtr sizeOfTablePointer = TestSizeOfTablePointer();
            var sizeOfIntPtrAsIntPtr = (IntPtr)IntPtr.Size;
            if (sizeOfTablePointer != sizeOfIntPtrAsIntPtr)
            {
                throw new TableException(String.Format(CultureInfo.InvariantCulture, "The Table* size{0} does not match the size of IntPtr{1}", sizeOfTablePointer, sizeOfIntPtrAsIntPtr));
            }

            IntPtr sizeOfCharPointer = TestSizeOfCharPointer();
            if (sizeOfCharPointer != sizeOfIntPtrAsIntPtr)
            {
                throw new TableException(String.Format(CultureInfo.InvariantCulture, "The Char* size{0} does not match the size of IntPtr{1}", sizeOfCharPointer, sizeOfIntPtrAsIntPtr));
            }

            IntPtr sizeOfInt64T = Testsizeofint64_t();
            var sizeOfLong = (IntPtr)sizeof(long);

            if (sizeOfInt64T != sizeOfLong)
            {
                throw new TableException(String.Format(CultureInfo.InvariantCulture, "The Int64_t size{0} does not match the size of long{1}", sizeOfInt64T, sizeOfLong));
            }


            IntPtr sizeOfTimeT = TestSizeOfTime_T();
            var sizeOfTimeTReceiverType = (IntPtr)sizeof(Int64);//we put time_t into an Int64 before we convert it to DateTime - we expect the time_t to be of type 64 bit integer

            if (sizeOfTimeT != sizeOfTimeTReceiverType)
            {
                throw new TableException(String.Format(CultureInfo.InvariantCulture, "The c++ time_t size({0}) does not match the size of the C# recieving type int64 ({1})", sizeOfTimeT, sizeOfTimeTReceiverType));
            }

            IntPtr sizeOffloatPlus = TestSizeOfFloat();
            var sizeOfFloatSharp = (IntPtr)sizeof(float);

            if (sizeOffloatPlus != sizeOfFloatSharp)
            {
                throw new TableException(String.Format(CultureInfo.InvariantCulture, "The c++ float size{0} does not match the size of C# float{1}", sizeOffloatPlus, sizeOfFloatSharp));
            }

            long sizeOfPlusDouble = (long)TestSizeOfDouble();
            long sizeOfSharpDouble = sizeof (double);

            if (sizeOfPlusDouble != sizeOfSharpDouble)
            {
                throw new TableException(String.Format(CultureInfo.InvariantCulture, "The c++ double size({0}) does not match the size of the C# recieving type Double ({1})", sizeOfPlusDouble, sizeOfSharpDouble));
            }



        }

        //if something is wrong with interop or C# marshalling, this method will throw an exception
        public static void TestInterop()
        {

            //that was the sizes of the basic types. Now check if parametres are sent and recieved in the correct sequence


            TestInteropSizes();

            IntPtr testres = TestGetFiveParametres((IntPtr)1, (IntPtr)2, (IntPtr)3, (IntPtr)4, (IntPtr)5);
            if ((long)testres != 1)
            {
                throw new ArgumentException("parameter sequence", String.Format(CultureInfo.InvariantCulture, "The c++ parameter sequence appears to be broken. called test_get_five_parametres(1,2,3,4,5) expected 1 as a return value, got {0}", testres));
            }

            float floatmaxcpp = TestFloatMax();
            const float floatmaxcs = float.MaxValue;
            if (floatmaxcpp > floatmaxcs || floatmaxcpp<floatmaxcs)
            {
                throw new ArgumentException("float",
                                            String.Format(CultureInfo.InvariantCulture,"The c++ max float value seems to be {0:r} while the C# is {1:r}", floatmaxcpp,floatmaxcs));
            }

            //current documentation on c# Sigle/float http://msdn.microsoft.com/en-us/library/system.single.aspx
            float floatmincpp = TestFloatMin();
            const float floatmincs = float.MinValue;
            if (floatmincpp > floatmincs || floatmincpp < floatmincs)
            {
                throw new ArgumentException("float",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ min float value seems to be {0:r} while the C# is {1:r}", floatmincpp, floatmincs));
            }

            const float float42 = 42f;
            float cppfortytwo = TestFloatReturn(float42);
            if (float42 > cppfortytwo || float42 < cppfortytwo)
            {
                throw new ArgumentException("float",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ float 42f was returned as {0:r} while the C# value is {1:r}", float42, cppfortytwo));
            }




            double doublemaxcpp = TestDoubleMax();
            const double doublemaxcs = double.MaxValue;
            if (doublemaxcpp > doublemaxcs || doublemaxcpp < doublemaxcs)
            {
                throw new ArgumentException("double",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ max double value seems to be {0:r} while the C# is {1:r}", doublemaxcpp, doublemaxcs));
            }

            
            double doublemincpp = TestDoubleMin();
            const double doublemincs = double.MinValue;
            if (doublemincpp > doublemincs || doublemincpp < doublemincs)
            {
                throw new ArgumentException("double",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ min double value seems to be {0:r} while the C# is {1:r}", doublemincpp, doublemincs));
            }

            const double double42 = 42f;
            double cppfortytwodouble = TestDoubleReturn(double42);
            if (double42 > cppfortytwodouble || double42 < cppfortytwodouble)
            {
                throw new ArgumentException("double",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ double 42f was returned as {0:r} while the C# value is {1:r}", double42, cppfortytwodouble));
            }




            //very well defined in C#. Should also be so in c++ so this should never fail  http://msdn.microsoft.com/en-us/library/ctetwysk.aspx
            long longmaxcpp = TestLongMax();
            const long longmaxcs = long.MaxValue;
            if (longmaxcpp != longmaxcs)
            {
                throw new ArgumentException("long",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ max int64_t (mapped to long) value seems to be {0:r} while the C# long is {1:r}", longmaxcpp, longmaxcs));
            }

            
            long longmincpp = TestLongMin();
            const long longmincs = long.MinValue;
            if (longmincpp != longmincs)
            {
                throw new ArgumentException("long",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ min int64_t (mapped to long) value seems to be {0:r} while the C# is {1:r}", longmincpp, longmincs));
            }

            const long long42 = 42;
            long cppfortytwolong = TestLongReturn(long42);
            if (long42!=cppfortytwolong)
            {
                throw new ArgumentException("long",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ int64_t 42 (mapped to long) was returned as {0:r} while the C# value is {1:r}", long42, cppfortytwolong));
            }





            IntPtr sizeTMaxCpp = TestSizeTMax();
            IntPtr sizeTMaxCs = IntPtr.Zero;
            sizeTMaxCs = sizeTMaxCs - 1;

            if (sizeTMaxCpp != sizeTMaxCs )
            {
                throw new ArgumentException("size_t",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ max size_t value seems to be {0:r} while the C# is {1:r}", sizeTMaxCpp, sizeTMaxCs));
            }

            IntPtr sizeTMinCpp = TestSizeTMin();
            IntPtr sizeTMinCs = IntPtr.Zero;
            if (sizeTMaxCpp != sizeTMaxCs)
            {
                throw new ArgumentException("size_t",
                                            String.Format(CultureInfo.InvariantCulture, "The c++ min size-t value seems to be {0:r} while the C# is {1:r}", sizeTMinCpp, sizeTMinCs));
            }

            var sizeTCs = (IntPtr)42;
            var sizeTCpp = TestSizeTReturn(sizeTCs);
            if (sizeTCpp!=sizeTCs)
            {
                throw new ArgumentException("Size_t",String.Format(CultureInfo.InvariantCulture, "The c++ size_t 42 was returned as {0:r} while the C# value is {1:r}", sizeTCpp, sizeTCs));
            }


            
            const DataType test = DataType.Binary;
            var test2 = TestReturnDataType(test);
            if (test != test2)
            {
                throw new ArgumentException("DataType", String.Format(CultureInfo.InvariantCulture, "The c++ returned Datatype  {0} is not the same as was sent by c# {1}", test2, test));
            }


            if (TestReturnFalseBool())
            {
                throw new ArgumentException("c++ TestReturnFalseBool returned true");
            }

            if (!TestReturnTrueBool())
            {
                throw new ArgumentException("c++ TestReturnTrueBool returned false");
            }

            if (!TestReturnBoolean(true))
            {
                throw new ArgumentException("sent true to testreturnboolean, got false back");
            }

            if (TestReturnBoolean(false))
            {
                throw new ArgumentException("sent false to testreturnboolean, got true back");
            }

        }
    }
}