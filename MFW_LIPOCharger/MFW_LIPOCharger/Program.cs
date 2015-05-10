using System;
using System.IO;
using System.Text;
using System.Threading;

using Microsoft.SPOT;
using Microsoft.SPOT.IO;
using Microsoft.SPOT.Hardware;

using Microsoft.SPOT.Presentation;
using Microsoft.SPOT.Presentation.Media;

using GHI.Pins;

namespace MFW_LIPOCharger
{


    public class Program
    {
        // const
        private const int NUMBATT = 2;
        private const int RCURR = 1;

        // current param. 
        /* Li-Ion batteries are typically charged between 0.5 C and 1 C in the constant-current stage, 
         * with the value determined by the battery manufacturer. 
         * For example, a battery rated 700 mAh can usually be charged at 350 mA, 
         * but this may be as high as 700 mA, depending on the manufacturer’s specification
        */
        private const int BATTCURR_ENERGY = 1400;           // 1400mAh Energy battery
        private const int PRECURR = 65;
        private const int CHGCURR = (BATTCURR_ENERGY / 2);  // 0.5C low charge mode
        private const int MANTCURR = 50;
        private const int MINCURR = 30;
        private const int OFFSCURR = ((CHGCURR*2)/100);     // 2% precision

        // voltage param.
        private const int VBATT_ALM = 4600;
        private const int VBATT_NOM = 4100;                 // 4.1V li-ion, 4.2V lipo
        private const int VBATT_PRECHG = 3000;
        private const int VBATT_MIN = 1000;
        private const int OFFSVOLT = 10;                    // +/-0.01V

        // led indicator
        private static OutputPort LED1 = new OutputPort(EMX.IO71, false);
        private static OutputPort LED2 = new OutputPort(EMX.IO9, false);

        // rele switch battery
        private static OutputPort RELE = new OutputPort(EMX.IO74, false);

        // analog input
        private static AnalogInput Ain5 = new AnalogInput(Cpu.AnalogChannel.ANALOG_5);
        private static AnalogInput Ain6 = new AnalogInput(Cpu.AnalogChannel.ANALOG_6);

        // battery idx
        private static int battIdx;

        // voltage battery
        private static int[] vBatt = { 0, 0 };

        // current battery
        private static int[] iBatt = { 0, 0 };


        // Thread for led indicator (not used)
        private static Thread ThreadShowParam;
        private static void ShowValues()
        {
            throw new NotImplementedException();
        }


        // Timers
        private static Timer ledTimer;
        private static TimerCallback tmrCallBack;
        private static AutoResetEvent autoEvent;

        private static bool blink;
        private static void TimerIRQ(object state)
        {
            blink = !blink;

            if (battIdx == 0)
            {
                LED1.Write(blink);
                LED2.Write(false);
            }
            else
            {
                //LED1.Write(false);
                LED2.Write(blink);
            }
        }

        public static void MeasureBattery()
        {
            const double AVG = 4.0;
            double ad5, ad6, mVr; 
            int mV5, mV6;


            // param read
            ad5 = 0;
            ad6 = 0;
            for (int i = 0; i < 4; i++)
            {
                ad5 += Ain5.Read();
                ad6 += Ain6.Read();
            }

            ad5 /= AVG;
            ad6 /= AVG;

            mV6 = (int)(3300.0 * ad6 + 0.5);
            iBatt[battIdx] = mV6 / RCURR;

            mV5 = (int)(3300.0 * ad5 + 0.5);
            mVr = (3300.0 * ad5 * (10.0 + 15.0)) / 15.0 + 0.5;
            vBatt[battIdx] = (int)mVr - mV6;

        }

        public static class PWMControl// : PWM 
        {

            private const double DC_INC = 0.001;

            private static double dc;
            private static PWM PWMCharge;

            /*public PWMControl() : base(Cpu.PWMChannel.PWM_1, 192000, dc, false)
            {
                dc = 0;
                PWMCharge = new PWM(Cpu.PWMChannel.PWM_1, 192000, dc, false);
            }*/

            public static void InitModule()
            {
                dc = 0;
                PWMCharge = new PWM(Cpu.PWMChannel.PWM_1, 192000, dc, false);
                PWMCharge.Start();
            }

            public static bool SetDuty(double d)
            {
                if (d<0 || d>1)
                    return false;

                PWMCharge.DutyCycle = d;
                dc = d;

                Debug.Print("DC=" + dc);
                return true;
            }

            public static double GetDuty()
            {
                return dc;
            }

            public static bool IncrDuty()
            {
                return SetDuty(dc + DC_INC);
            }

            public static bool DecrDuty()
            {
                return SetDuty(dc - DC_INC);
            }

            public static bool IncDirDuty(bool dir)
            {   
                 return SetDuty(dc + (dir ? +DC_INC : -DC_INC));
            }

            public static string GetDCString()
            {
                return "PWM DC= " + (int)(dc * 1000) + "%0";
            }
        }

        public class StateM
        {

            public enum STATE { CHECK_ERR, CHECK_ALREADYCHG, PRE_CHARGE, FAST_CHARGE, MANT1, MANT2, ERROR_CHARGE, FINISH };


            public static STATE state;
            public static bool stsflg;


            // costructor
            public StateM()
            {
                state = STATE.CHECK_ERR;
                stsflg = true;
            }

            // set state
            public static void Set(STATE s)
            {
                state = s;
                stsflg = true;
            }

            // get state
            public static STATE Get()
            {
                return state;
            }

        }


        public static void Main()
        {

            int min = 0;

            // watchdog enable with 5 second timeout
            GHI.Processor.Watchdog.Enable(5 * 1000);

            // PWM
            PWMControl.InitModule();

            // state machine
            //StateM();

            // num. battery (serial)
            battIdx = 0;

            // init thread
            ThreadShowParam = new Thread(Program.ShowValues);
            //ledThread.Start();

            // init Timer (http://blog.mark-stevens.co.uk/2013/01/netmf-timers/)
            tmrCallBack = new TimerCallback(TimerIRQ);
            autoEvent = new AutoResetEvent(false);
            ledTimer = new Timer(tmrCallBack, null, 0, 1000);
            //ledTimer = new Timer(tmrCallBack, autoEvent,Timeout.Infinite, 1000);

            // init LCD
            Bitmap LCD = new Bitmap(SystemMetrics.ScreenWidth, SystemMetrics.ScreenHeight);
            Font font = Resources.GetFont(Resources.FontResources.NinaB);

            // debug
            Debug.Print("Program Started");

            // clear display
            LCD.Clear();
            LCD.Flush();

            // init state machine ctrl
            StateM.Set(StateM.STATE.CHECK_ERR);
            

            // loop
            while (true)
            {
                // reset watchdog
                GHI.Processor.Watchdog.ResetCounter();

                // param read
                MeasureBattery();

                switch ( StateM.Get() )
                {
		            // vbatt < 1V
		            case StateM.STATE.CHECK_ERR:
                        if (vBatt[battIdx] < VBATT_MIN)
                        {
                            LCD.DrawText("ERROR BATTERY n° " + (battIdx+1), font, Colors.White, 10, 50);
                            LCD.DrawText("Vbatt[" + battIdx + "]= " + vBatt[battIdx] + "mV", font, Colors.White, 10, 80);
                            LCD.Flush();

                            PWMControl.SetDuty(0);
                            StateM.Set(StateM.STATE.ERROR_CHARGE);
                            break;
                        }
                        StateM.Set(StateM.STATE.CHECK_ALREADYCHG);
			            break;

		            // vbatt > 4.6V
                    case StateM.STATE.CHECK_ALREADYCHG: // battery charged
                        if (vBatt[battIdx] > VBATT_ALM)
                        {
                            LCD.Clear();
                            LCD.DrawText("BATTERY n° " + (battIdx+1) + " CHARGED!", font, Colors.White, 10, 50);
                            LCD.DrawText("Vbatt[" + battIdx + "]= " + vBatt[battIdx] + "mV", font, Colors.White, 10, 80);
                            LCD.Flush();

                            PWMControl.SetDuty(0);
                            ledTimer.Change(Timeout.Infinite, 1000);
                            StateM.Set(StateM.STATE.FINISH);
                            break;
                        }
                        StateM.Set(StateM.STATE.PRE_CHARGE);
			            break;

		            // vbatt < 3V ==> precharge 65mA
                    case StateM.STATE.PRE_CHARGE:

                        if (vBatt[battIdx] < VBATT_PRECHG)
                        {
                            if (StateM.stsflg)
                            {
                                LCD.Clear();
                                LCD.DrawText("BATTERY PRE-CHARGE...", font, Colors.White, 10, 20);
                                LCD.DrawText("Vbatt[" + battIdx + "]= " + vBatt[battIdx] + "mV", font, Colors.White, 10, 50);
                                LCD.Flush();

                                ledTimer.Change(0, 1000);
                                PWMControl.SetDuty(0);
                                StateM.stsflg = false;
                            }

                            PWMControl.IncDirDuty(iBatt[battIdx] < PRECURR);
                            break;
                        }
                        StateM.Set(StateM.STATE.FAST_CHARGE);
                        break;

                    // CONSTANT CURRENT: vbatt > 3V && vbatt < 4.2V ==> charge 350mA
                    case StateM.STATE.FAST_CHARGE:
                        if (vBatt[battIdx] < VBATT_NOM || iBatt[battIdx] < (3*CHGCURR)/4)
                        {
                            if (StateM.stsflg)
                            {
                                LCD.Clear();
                                LCD.DrawText("BATTERY FAST CHARGING: CONST. CURRENT", font, Colors.White, 10, 20);
                                LCD.DrawText("Vbatt[" + battIdx + "]= " + vBatt[battIdx] + "mV", font, Colors.White, 10, 50);
                                LCD.Flush();

                                ledTimer.Change(0, 500);
                                StateM.stsflg = false;
                            }

                            if (iBatt[battIdx] < (CHGCURR - OFFSCURR))
                            {
                                if (PWMControl.IncrDuty())
                                    break;
                            }
                            else if (iBatt[battIdx] > (CHGCURR + OFFSCURR))
                            {
                                if (PWMControl.DecrDuty())
                                    break;
                            }

                            LCD.Clear();
                            LCD.DrawText("BATTERY FAST CHARGING: CONST. CURRENT", font, Colors.White, 10, 20);
                            LCD.DrawText("Vbatt[" + battIdx + "]= " + vBatt[battIdx] + "mV", font, Colors.White, 10, 50);
                            LCD.DrawText("Ibatt[" + battIdx + "]= " + iBatt[battIdx] + "mA", font, Colors.White, 10, 80);
                            LCD.DrawText(PWMControl.GetDCString(), font, Colors.White, 10, 110);
                            LCD.Flush();

                            Thread.Sleep(500);
                            break;
                        }
                        StateM.Set(StateM.STATE.MANT1);
                        break;


                    // CONSTANT VOLTAGE: vbatt >= 4.2 && Ibatt >= 50mA ==> cont. voltage charging vout=4.2V
                    case StateM.STATE.MANT1:
                        if (StateM.stsflg)
                        {
                            LCD.Clear();
                            LCD.DrawText("BATTERY MANT1: CONST. VOLTAGE", font, Colors.White, 10, 20);
                            LCD.DrawText("Vbatt[" + battIdx + "]= " + vBatt[battIdx] + "mV", font, Colors.White, 10, 50);
                            LCD.Flush();

                            ledTimer.Change(0, 100);
                            StateM.stsflg = false;
                        }

                        if (iBatt[battIdx] > MANTCURR)
                        {
                            if (vBatt[battIdx] < (VBATT_NOM - OFFSVOLT))
                            {
                                if (PWMControl.IncrDuty())
                                    break;
                            }
                            else if (vBatt[battIdx] > (VBATT_NOM + OFFSVOLT))
                            {
                                if (PWMControl.DecrDuty())
                                    break;
                            }

                            LCD.Clear();
                            LCD.DrawText("BATTERY MANT1: CONST. VOLTAGE", font, Colors.White, 10, 20);
                            LCD.DrawText("Vbatt[" + battIdx + "]= " + vBatt[battIdx] + "mV", font, Colors.White, 10, 50);
                            LCD.DrawText("Ibatt[" + battIdx + "]= " + iBatt[battIdx] + "mA", font, Colors.White, 10, 80);
                            LCD.DrawText(PWMControl.GetDCString(), font, Colors.White, 10, 110);
                            LCD.Flush();

                            Thread.Sleep(500);
                            break;
                        }
                        //StateM.Set(StateM.STATE.MANT2);          
                        StateM.Set(StateM.STATE.FINISH);          
			            break;

		            // vbatt >= 4.2 && Ibatt < 50mA ==> set vout=4.2V for 50min.
                    case StateM.STATE.MANT2:
                        if (vBatt[battIdx] >= VBATT_NOM)
                        {
                            if (StateM.stsflg)
                            {
                                LCD.Clear();
                                LCD.DrawText("BATTERY MANT2... wait 50min", font, Colors.White, 10, 20);
                                LCD.Flush();

                                min = 50 * 60 * 2;

                                StateM.stsflg = false;
                            }

                            PWMControl.IncDirDuty(iBatt[battIdx] <= MANTCURR);

                            LCD.Clear();
                            LCD.DrawText("BATTERY MANT2", font, Colors.White, 10, 20);
                            LCD.DrawText("Vbatt[" + battIdx + "]= " + vBatt[battIdx] + "mV", font, Colors.White, 10, 50);
                            LCD.DrawText("Ibatt[" + battIdx + "]= " + iBatt[battIdx] + "mA", font, Colors.White, 10, 80);
                            LCD.DrawText(PWMControl.GetDCString(), font, Colors.White, 10, 110);
                            LCD.DrawText("Minutes... " + (min/120), font, Colors.White, 10, 110);
                            LCD.Flush();

                            min--;
                            Thread.Sleep(500);
                            break;
                        }
                        StateM.Set(StateM.STATE.FINISH);  
			            break;

		            // charge complete
                    case StateM.STATE.FINISH:
                        if (battIdx == 0) {

                            if (StateM.stsflg)
                            {
                                LCD.Clear();
                                LCD.DrawText("BATTERY 1 CHARGED!", font, Colors.White, 10, 20);
                                LCD.Flush();

                                StateM.stsflg = false;
                            }

                            RELE.Write(true);
                            LED1.Write(true);
                            StateM.Set(StateM.STATE.CHECK_ERR);  
                            battIdx = 1;
                        }
                        else
                        {
                            if (StateM.stsflg)
                            {
                                LCD.Clear();
                                LCD.DrawText("BATTERY FULL CHARGED!!!", font, Colors.White, 10, 20);
                                LCD.DrawText("Vbatt[" + battIdx + "]= " + vBatt[battIdx] + "mV", font, Colors.White, 10, 50);
                                LCD.DrawText("Ibatt[" + battIdx + "]= " + iBatt[battIdx] + "mA", font, Colors.White, 10, 80);
                                LCD.Flush();

                                RELE.Write(false);
                                LED2.Write(true);
                                StateM.stsflg = false;
                            }

                            Thread.Sleep(2000);
                        }
			            break;

		            default:
			            break;

                }
            }

            // Sleep forever
            //Thread.Sleep(Timeout.Infinite);
            //Debug.Print(Resources.GetString(Resources.StringResources.String1));
        }



    }
}
