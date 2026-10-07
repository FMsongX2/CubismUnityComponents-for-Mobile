/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at http: //live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
// Cubism 설정 파일의 제한된 JSON을 읽는 경량 parser입니다.
// ASCII 문자와 기본 JSON 형태만 지원하며 비ASCII 문자와 e 지수 표기는 지원하지 않습니다.


using System;
using System.Collections.Generic;
using System.Text;


namespace Live2D.Cubism.Framework.Json
{
    public class CubismJsonParser
    {
        #region variable

        private char[] buffer;

        private int length;

        private int line_count = 0;

        private Value root;

        #endregion

        /// 입력: jsonBytes(char[]); 반환: 없음.
        public CubismJsonParser(char[] jsonBytes)
        {
            this.buffer = jsonBytes;
            this.length = jsonBytes.Length;
        }

        #region Parse Functionn

        /// 입력: 없음; 반환: Value.
        public Value Parse()
        // 파싱에 실패하면 현재 줄 번호와 원래 오류를 포함한 예외를 던집니다.
        {
            try
            {
                var ret = new int[1];
                root = ParseValue(buffer, length, 0, ret);
                return root;
            }
            catch (Exception e)
            {
                throw new Exception("json error " + "@line:" + line_count + " / " + e.Message, e);
            }
        }


        /// 입력: jsonBytes(char[]); 반환: Value.
        public static Value ParseFromBytes(char[] jsonBytes)
        // 입력 형식이 잘못되면 Parse에서 만든 예외가 호출자에게 전달됩니다.
        {
            var jp = new CubismJsonParser(jsonBytes);
            var ret = jp.Parse();
            return ret;
        }


        /// 입력: jsonString(String); 반환: Value.
        public static Value ParseFromString(String jsonString)
        // 입력 형식이 잘못되면 Parse에서 만든 예외가 호출자에게 전달됩니다.
        {
            var buffer = jsonString.ToCharArray();
            var jp = new CubismJsonParser(buffer);
            var ret = jp.Parse();
            return ret;
        }


        /// 입력: str(char[]), length(int), pos(int), endPos(int[]); 반환: String.
        private static String ParseString(char[] str, int length, int pos, int[] endPos)
        // 닫는 따옴표가 없거나 지원하지 않는 escape가 나오면 예외를 던집니다.
        {
            char c, c2;
            StringBuilder stringBuffer = null;
            var startPos = pos; // 아직 stringBuffer에 복사하지 않은 구간의 시작 위치입니다.

            for (var i = pos; i < length; i++)
            {
                c = (char)(str[i]);

                switch (c)
                {
                    case '\"': // escape 처리되지 않은 닫는 따옴표이므로 문자열 읽기를 끝냅니다.
                        endPos[0] = i + 1; // 호출자가 닫는 따옴표 다음 문자부터 이어 읽게 합니다.
                        if (stringBuffer != null)
                        {
                            if (i > startPos) stringBuffer.Append(new string(str, startPos, i - startPos)); // 마지막 미복사 구간을 결과에 붙입니다.
                            return stringBuffer.ToString();
                        }
                        else
                        {
                            return new string(str, pos, i - pos);
                        }

                    case '\\': // escape 문자를 실제 문자로 바꾸기 위해 별도 버퍼를 사용합니다.
                        if (stringBuffer == null)
                        {
                            stringBuffer = new StringBuilder();
                        }
                        if (i > startPos) stringBuffer.Append(new string(str, startPos, i - startPos)); // escape 앞의 미복사 구간을 결과에 붙입니다.

                        i++; // 역슬래시와 그 다음 문자를 한 묶음으로 소비합니다.

                        if (i < length)
                        {
                            c2 = (char)(str[i]);
                            switch (c2)
                            {
                                case '\\': stringBuffer.Append('\\'); break;
                                case '\"': stringBuffer.Append('\"'); break;
                                case '/': stringBuffer.Append('/'); break;
                                case 'b': stringBuffer.Append('\b'); break;
                                case 'f': stringBuffer.Append('\f'); break;
                                case 'n': stringBuffer.Append('\n'); break;
                                case 'r': stringBuffer.Append('\r'); break;
                                case 't': stringBuffer.Append('\t'); break;
                                case 'u':
                                    throw new Exception("parse string/unicode escape not supported");
                            }
                        }
                        else
                        {
                            throw new Exception("parse string/escape error");
                        }
                        startPos = i + 1; // 처리한 escape 다음 문자부터 새 미복사 구간을 시작합니다.
                        break;
                }
            }
            throw new Exception("parse string/illegal end");
        }


        /// 입력: buffer(char[]), length(int), pos(int), endPos(int[]); 반환: Value.
        private Value ParseObject(char[] buffer, int length, int pos, int[] endPos)
        // 키·콜론·종료 문법이 잘못되면 현재 객체 파싱을 중단하고 예외를 던집니다.
        {
            var ret = new Dictionary<String, Value>();

            // 객체 항목을 "키 : 값" 순서로 읽어 ret에 추가합니다.
            String key = null;
            char c;
            var i = pos;
            var ret_endPos = new int[1];
            var ok = false;

            // 쉼표 뒤에 다음 키가 있는 동안 객체 항목을 계속 읽습니다.
            for (; i < length; i++)
            {
                // 첫 번째 탐색은 다음 키의 여는 따옴표를 찾습니다.
                for (; i < length; i++)
                {
                    c = (char)(buffer[i]);

                    switch (c)
                    {
                        case '\"':
                            key = ParseString(buffer, length, i + 1, ret_endPos);
                            i = ret_endPos[0];
                            ok = true;
                            goto EXIT_FOR_LOOP1;
                        case '}': endPos[0] = i + 1; return new Value(ret); // 키가 나오기 전 닫히면 빈 객체를 반환합니다.
                        case ':': throw new Exception("illegal ':' position");
                        default: break; // 키 시작 전의 공백 문자는 건너뜁니다.
                    }
                }
                EXIT_FOR_LOOP1:

                if (!ok)
                {
                    throw new Exception("key not found");
                }
                ok = false;

                // 두 번째 탐색은 읽은 키 뒤의 콜론을 확인합니다.
                for (; i < length; i++)
                {
                    c = (char)(buffer[i]);

                    switch (c)
                    {
                        case ':': ok = true; i++; goto EXIT_FOR_LOOP2;
                        case '}': throw new Exception("illegal '}' position");
                        case '\n': line_count++; break;
                        default: break; // 콜론 앞의 공백 문자는 건너뜁니다.
                    }
                }
                EXIT_FOR_LOOP2:

                if (!ok)
                {
                    throw new Exception("':' not found");
                }

                // 콜론 다음 위치에서 값 하나를 읽고 현재 키에 연결합니다.
                Value value = ParseValue(buffer, length, i, ret_endPos);
                i = ret_endPos[0];
                ret.Add(key, value);

                // 세 번째 탐색은 다음 항목의 쉼표 또는 객체의 닫는 중괄호를 찾습니다.
                for (; i < length; i++)
                {
                    c = (char)(buffer[i]);

                    switch (c)
                    {
                        case ',': goto EXIT_FOR_LOOP3; // 다음 키와 값을 읽도록 바깥 반복으로 돌아갑니다.
                        case '}': endPos[0] = i + 1; return new Value(ret); // 객체가 끝났으므로 누적한 사전을 반환합니다.
                        case '\n': line_count++; break;
                        default: break; // 쉼표나 닫는 중괄호 전의 공백 문자는 건너뜁니다.
                    }
                }
                EXIT_FOR_LOOP3: ;

            }

            throw new Exception("illegal end of ParseObject");
        }


        /// 입력: buffer(char[]), length(int), pos(int), endPos(int[]); 반환: Value.
        private Value ParseArray(char[] buffer, int length, int pos, int[] endPos)
        // 배열 종료 문자가 없으면 파싱을 중단하고 예외를 던집니다.
        {
            var ret = new List<Value>();
            var i = pos;
            char c;
            var ret_endPos = new int[1];

            // 쉼표 뒤에 다음 값이 있는 동안 배열 항목을 계속 읽습니다.
            for (; i < length; i++)
            {
                // 현재 위치에서 값 하나를 읽고 null이 아니면 결과 목록에 추가합니다.
                var value = ParseValue(buffer, length, i, ret_endPos);
                i = ret_endPos[0];
                if (value != null)
                {
                    ret.Add(value);
                }

                // 값 뒤에서 다음 항목의 쉼표 또는 배열의 닫는 대괄호를 찾습니다.
                for (; i < length; i++)
                {
                    c = (char)(buffer[i]);

                    switch (c)
                    {
                        case ',': goto EXIT_FOR_LOOP3; // 다음 배열 값을 읽도록 바깥 반복으로 돌아갑니다.
                        case ']': endPos[0] = i + 1; return new Value(ret); // 배열이 끝났으므로 누적한 목록을 반환합니다.
                        case '\n': line_count++; break;
                        default: break; // 쉼표나 닫는 대괄호 전의 공백 문자는 건너뜁니다.
                    }
                }
                EXIT_FOR_LOOP3: ;
            }

            throw new Exception("illegal end of ParseObject");
        }


        /// 입력: str(char[]), length(int), pos(int), endPos(int[]); 반환: double.
        public static double strToDouble(char[] str, int length, int pos, int[] endPos)
        {
            // 호출자가 전달한 length 범위 안에서만 숫자를 읽습니다.
            var i = pos;
            var minus = false; // 선두 음수 부호를 읽었는지 보관합니다.
            var period = false;
            var v1 = 0.0;

            // 첫 문자가 음수 부호이면 부호를 기록하고 숫자 본문으로 이동합니다.
            var c = (char)(str[i]);
            if(c == '-')
            {
                minus = true;
                i++;
            }

            // 소수점이나 구분 문자를 만날 때까지 정수부를 누적합니다.
            for (; i < length; i++)
            {
                c = (char)(str[i]);

                switch (c)
                {
                    case '0': v1 = v1 * 10; break;
                    case '1': v1 = v1 * 10 + 1; break;
                    case '2': v1 = v1 * 10 + 2; break;
                    case '3': v1 = v1 * 10 + 3; break;
                    case '4': v1 = v1 * 10 + 4; break;
                    case '5': v1 = v1 * 10 + 5; break;
                    case '6': v1 = v1 * 10 + 6; break;
                    case '7': v1 = v1 * 10 + 7; break;
                    case '8': v1 = v1 * 10 + 8; break;
                    case '9': v1 = v1 * 10 + 9; break;
                    case '.':
                        period = true;
                        i++;
                        goto EXIT_FOR_LOOP;
                    default: // 줄바꿈·쉼표 같은 구분 문자를 만나면 숫자 읽기를 끝냅니다.
                        goto EXIT_FOR_LOOP;
                }
            }

            EXIT_FOR_LOOP:

            // 소수점이 있었다면 자리 가중치를 0.1씩 줄이며 소수부를 누적합니다.
            if (period)
            {
                var mul = 0.1;

                // 숫자가 아닌 구분 문자를 만날 때까지 소수 자릿수를 읽습니다.
                for (; i < length; i++)
                {
                    c = (char)(str[i]);

                    switch (c)
                    {
                        case '0': break;
                        case '1': v1 += mul * 1; break;
                        case '2': v1 += mul * 2; break;
                        case '3': v1 += mul * 3; break;
                        case '4': v1 += mul * 4; break;
                        case '5': v1 += mul * 5; break;
                        case '6': v1 += mul * 6; break;
                        case '7': v1 += mul * 7; break;
                        case '8': v1 += mul * 8; break;
                        case '9': v1 += mul * 9; break;
                        default: // 줄바꿈·쉼표 같은 구분 문자를 만나면 소수부 읽기를 끝냅니다.
                            goto EXIT_FOR_LOOP2;
                    }
                    mul *= 0.1;
                }

                EXIT_FOR_LOOP2:;
            }

            if (minus)
            {
                v1 = -v1;
            }

            endPos[0] = i;
            return v1;
        }


        /// 입력: buffer(char[]), length(int), pos(int), endPos(int[]); 반환: Value.
        private Value ParseValue(char[] buffer, int length, int pos, int[] endPos)
        // 값의 첫 유효 문자에 따라 전용 파서를 호출하며 잘못된 구분 위치에서는 예외를 던집니다.
        {
            Value obj;
            var i = pos;
            for (; i < length; i++)
            {
                var c = (char)(buffer[i]);

                switch (c)
                {
                    case '-':
                    case '.':
                    case '0':
                    case '1':
                    case '2':
                    case '3':
                    case '4':
                    case '5':
                    case '6':
                    case '7':
                    case '8':
                    case '9':
                        var f = strToDouble(buffer, length, i, endPos);
                        return new Value(f);
                    case '\"':
                        obj = new Value(ParseString(buffer, length, i + 1, endPos)); // 여는 따옴표 다음부터 문자열을 읽습니다.
                        return obj;
                    case '[':
                        obj = ParseArray(buffer, length, i + 1, endPos);
                        return obj;
                    case ']': // 배열 파서가 같은 닫는 대괄호를 처리하도록 여기서는 값을 만들지 않습니다.
                        // 이 분기에서는 obj를 만들지 않고 null을 반환합니다.
                        endPos[0] = i; // 호출자가 같은 닫는 대괄호를 다시 처리하게 위치를 유지합니다.
                        return null;
                    case '{':
                        obj = ParseObject(buffer, length, i + 1, endPos);
                        return obj;
                    case 'n': // null 토큰 길이를 확인하고 null 값을 반환합니다.
                        if (i + 3 < length) obj = null;
                        else throw new Exception("parse null");
                        return obj;
                    case 't': // true 토큰 길이를 확인하고 논리값을 만듭니다.
                        if (i + 3 < length) obj = new Value(true);
                        else throw new Exception("parse true");
                        return obj;
                    case 'f': // false 토큰 길이를 확인하고 논리값을 만듭니다.
                        if (i + 4 < length) obj = new Value(false);
                        else throw new Exception("parse false");
                        return obj;
                    case ',': // 값 없이 배열 구분자부터 나오면 잘못된 위치입니다.
                        throw new Exception("illegal ',' position");
                    case '\n': line_count++;
                        break;
                    case ' ':
                    case '\t':
                    case '\r':
                    default: // 값 앞의 공백 문자는 건너뜁니다.
                        break;
                }
            }

            // 유효한 값 없이 입력 끝에 도달하면 기존 호환 동작대로 null을 반환합니다.
            return null;
        }

        #endregion
    }


    public class Value
    {
        private Object _object;

        /// 입력: obj(Object); 반환: 없음.
        public Value(Object obj)
        {
            this._object = obj;
        }

        #region toString

        /// 입력: 없음; 반환: string.
        public string toString()
        {
            return toString("");
        }

        /// 입력: indent(string); 반환: string.
        public string toString(string indent)
        {
            if (_object is string)
            {
                return (string)_object;
            }

            // 목록은 각 원소를 한 줄씩 재귀 변환해 대괄호 안에 표시합니다.
            else if (_object is List<Value>)
            {
                string ret = indent + "[\n";
                foreach (Value v in ((List<Value>)_object))
                {
                    ret += indent + "    " + v.toString(indent + "    ") + "\n";
                }
                ret += indent + "]\n";
                return ret;
            }

            // 사전은 각 키와 값을 한 줄씩 재귀 변환해 중괄호 안에 표시합니다.
            else if (_object is Dictionary<string, Value>)
            {

                string ret = indent + "{\n";
                Dictionary<string, Value> vmap = (Dictionary<string, Value>)_object;
                foreach (KeyValuePair<string, Value> pair in vmap)
                {
                    Value v = pair.Value;
                    ret += indent + "    " + pair.Key + " : " + v.toString(indent + "    ") + "\n";
                }
                ret += indent + "}\n";
                return ret;
            }
            else
            {
                return "" + _object;
            }
        }

        #endregion

        #region toInt

        /// 입력: 없음; 반환: int.
        public int toInt()
        {
            return toInt(0);
        }

        /// 입력: defaultValue(int); 반환: int.
        public int toInt(int defaultValue)
        {
            return (_object is Double) ? (int)((Double)_object) : defaultValue;
        }

        #endregion

        #region ToFloat

        /// 입력: 없음; 반환: float.
        public float ToFloat()
        {
            return ToFloat(0);
        }

        /// 입력: defaultValue(float); 반환: float.
        public float ToFloat(float defaultValue)
        {
            return (_object is Double) ? (float)((Double)_object) : defaultValue;
        }

        #endregion

        #region ToDouble

        /// 입력: 없음; 반환: double.
        public double ToDouble()
        {
            return ToDouble(0);
        }

        /// 입력: defaultValue(double); 반환: double.
        public double ToDouble(double defaultValue)
        {
            return (_object is Double) ? ((Double)_object) : defaultValue;
        }

        #endregion

        #region toArray

        /// 입력: defalutV(List<Value>); 반환: List<Value>.
        public List<Value> GetVector(List<Value> defalutV)
        {
            return (_object is List<Value>) ? (List<Value>)_object : defalutV;
        }


        /// 입력: index(int); 반환: Value.
        public Value Get(int index)
        {
            return (_object is List<Value>) ? (Value)((List<Value>)_object)[index] : null;
        }

        #endregion

        #region toDictionary

        /// 입력: defalutV(Dictionary<string, Value>); 반환: Dictionary<string, Value>.
        public Dictionary<string, Value> GetMap(Dictionary<string, Value> defalutV)
        {
            return (_object is Dictionary<string, Value>) ? (Dictionary<string, Value>)_object : defalutV;
        }


        /// 입력: key(string); 반환: Value.
        public Value Get(string key)
        {
            if(_object is Dictionary<string, Value>)
            {
                if (((Dictionary<string, Value>)_object).ContainsKey(key)) return (Value)((Dictionary<string, Value>)_object)[key];
            }

            return null;
        }


        /// 입력: 없음; 반환: List<string>.
        public List<string> KeySet()
        {
            return (_object is Dictionary<string, Value>) ? new List<string>(((Dictionary<string, Value>)_object).Keys) : null;
        }


        /// 입력: 없음; 반환: Dictionary<string, Value>.
        public Dictionary<string, Value> ToMap()
        {
            return (_object is Dictionary<string, Value>) ? (Dictionary<string, Value>)_object: null;
        }

        #endregion

        #region check type

        /// 입력: 없음; 반환: bool.
        public bool isNull()    { return _object == null; }
        /// 입력: 없음; 반환: bool.
        public bool isBoolean() { return _object is Boolean; }
        /// 입력: 없음; 반환: bool.
        public bool isDouble()  { return _object is Double; }
        /// 입력: 없음; 반환: bool.
        public bool isString()  { return _object is string; }
        /// 입력: 없음; 반환: bool.
        public bool isArray()   { return _object is List<Value>; }
        /// 입력: 없음; 반환: bool.
        public bool isMap()     { return _object is Dictionary<string, Value>; }

        #endregion
    }
}
