using Assets.Kiwrious.Scripts;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;

#if UNITY_STANDALONE || UNITY_EDITOR
using UnityEditor.Hardware;
#endif

using UnityEngine;
using static Assets.Kiwrious.Scripts.Constants;

public class SerialReader : MonoBehaviour{

	public static SerialReader instance;

	private const int PACKET_SIZE = 26;
	private const int BAUD_RATE = 115200;
	private const int READ_TIMEOUT = 1500;
	private const int MAX_RETRY_ATTEMPTS = 128;
	private const int PACKET_HEADER_BYTE = 0X0a;
	private const int PACKET_FOOTER_BYTE = 0X0b;


	public Dictionary<SENSOR_TYPE, bool> kiwriousSensorStatus = new Dictionary<SENSOR_TYPE, bool>();

	public float voc1;
	public float voc2;
	public float conductivity;
	public float uv;
	public float lux;
	public float humidity;
	public float temperature;
	public float color_h;
	public float color_s;
	public float color_v;
	public float a_temperature;
	public float d_temperature;
	public float heart_rate;
	public byte[] rawData = new byte[26];

	public bool listen;
	public bool autoStart;

	private bool isReading = false;
	
	private Coroutine serialListener;

	[SerializeField]
	private string[] connectedSerialPorts;
	[SerializeField]
	private List<KiwriousSensor> kiwriousSensorRegistry = new List<KiwriousSensor>();
	[SerializeField]
	private List<KiwriousSensor> connectedKiwriousSensors = new List<KiwriousSensor>();

	private List<Thread> sensorReaders = new List<Thread>();
	private List<SerialPort> activePorts = new List<SerialPort>();
	private Dictionary<SENSOR_TYPE, Action<string, byte[]>> decodeMethods = new Dictionary<SENSOR_TYPE, Action<string, byte[]>>();

	void Awake() {
		instance = this;
#if UNITY_STANDALONE || UNITY_EDITOR
		Usb.DevicesChanged += onUSBConnectDisconenct;
#endif
    }

	// https://stephenhodgson.github.io/UnityCsReference/api/UnityEditor.Hardware.Usb.OnDevicesChangedHandler.html
#if UNITY_STANDALONE || UNITY_EDITOR
	void onUSBConnectDisconenct(UsbDevice[] usbDevices)
    {
        UsbDevice[] kiwriousDevices = Array.FindAll(usbDevices, d => d.vendorId == 1027 || d.vendorId == 1240);
        foreach (UsbDevice u in kiwriousDevices)
        {
            Debug.Log(u.name);
        }
    }
#endif

    void Start () {
		foreach (SENSOR_TYPE sensorType in Enum.GetValues(typeof(SENSOR_TYPE)))
		{
			kiwriousSensorStatus[sensorType] = false;
		}
		decodeMethods[SENSOR_TYPE.EC] = DecodeConductivity;
        decodeMethods[SENSOR_TYPE.CLIMATE] = DecodeHumidity;
        decodeMethods[SENSOR_TYPE.LIGHT] = DecodeUV;
        decodeMethods[SENSOR_TYPE.VOC] = DecodeVOC;
        decodeMethods[SENSOR_TYPE.COLOR] = DecodeColor;
		decodeMethods[SENSOR_TYPE.CARDIO] = DecodeCardio;
		decodeMethods[SENSOR_TYPE.THERMAL] = DecodeThermal;
		decodeMethods[SENSOR_TYPE.THERMAL2] = DecodeThermal2;
		if (autoStart) {
			StartSerialReader();
		}
	}

	public List<KiwriousSensor> GetConnectedKiwriousSensors() {
		return connectedKiwriousSensors;
	}

	public void StartSerialReader()
	{
		isReading = true;
		serialListener = StartCoroutine(ScanPorts());
	}

	public void StopSerialReader()
	{
		isReading = false;

		if (serialListener != null)
		{
			StopCoroutine(serialListener);
		}
		foreach (SerialPort s in activePorts)
		{
			if (s != null && s.IsOpen) {
				s.Close();
			}
		}
		foreach (Thread reader in sensorReaders)
		{
			try
			{
				reader.Join();
			}
			catch (Exception ex)
			{
				Debug.LogError(ex.Message);
			}
		}
	}

	IEnumerator ScanPorts()
	{
		Debug.Log("Scanning ports");
		while (listen && isReading)
		{
			yield return new WaitForSeconds(1);
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length > connectedSerialPorts.Length)
            {
                string[] newDevices = ports.Where(p => !connectedSerialPorts.Contains(p)).ToArray();
                foreach (string port in newDevices)
                {
                    ProcessConnectedSerialDevice(port);
                }
            }
            else if (ports.Length < connectedSerialPorts.Length)
            {
                string[] removedDevices = connectedSerialPorts.Where(p => !ports.Contains(p)).ToArray();
                foreach (string port in removedDevices)
                {
                    ProcessDisconnectedSerialDevice(port);
                }
            }
            connectedSerialPorts = ports;
        }
	}

	private void ProcessConnectedSerialDevice(string port) {
		if (kiwriousSensorRegistry.Any(s => s.Port == port))
		{
			KiwriousSensor connectedSensor = kiwriousSensorRegistry.Where(s => s.Port == port).FirstOrDefault();
			Debug.Log($"{connectedSensor.Name} sensor connected!");
			connectedKiwriousSensors.Add(connectedSensor);
			StartKiwriousSensorReader(port);
			return;
		}
		Thread newSensorScanner = new Thread(() => IdentifySerialDevice(port))
		{
			Name = port,
			IsBackground = true
		};
		newSensorScanner.Start();
		newSensorScanner.Join();
		if (IsKiwriousSensorConnectedAt(port)) {
			StartKiwriousSensorReader(port);
		}
	}

	private void StartKiwriousSensorReader(string port) {
		Thread newsensorReader = new Thread(() => ReadSensor(port))
		{
			Name = port,
			IsBackground = true
		};
		newsensorReader.Start();
		sensorReaders.Add(newsensorReader);
	}

	private void ProcessDisconnectedSerialDevice(string port) {
		if (connectedKiwriousSensors.Any(s => s.Port == port))
		{
			KiwriousSensor disconnectedSensor = connectedKiwriousSensors.Where(s => s.Port == port).FirstOrDefault();
			Debug.Log($"{disconnectedSensor.Name} sensor disconnected!");
			connectedKiwriousSensors.Remove(disconnectedSensor);
			kiwriousSensorStatus[(SENSOR_TYPE)disconnectedSensor.Type] = false;
			SerialPort activePort = activePorts.Where(s => s.PortName == port).FirstOrDefault();
			activePort.Close();
			activePorts.Remove(activePort);
			Thread reader = sensorReaders.Where(r => r.Name == port).FirstOrDefault();
			reader.Join();
			sensorReaders.Remove(reader);
		}
		else {
			Debug.Log($"Disconnected device at port [{port}] is not a kiwrious sensor");
		}
	}

	private void ReadSensor(string port) {
		
		Debug.Log($"Read {port}");
		SENSOR_TYPE sensorType = (SENSOR_TYPE)GetSensorTypeByPort(port);
		kiwriousSensorStatus[sensorType] = (true);
		SerialPort stream = new SerialPort(port, BAUD_RATE);
		activePorts.Add(stream);
		if (!stream.IsOpen)
		{
			stream.Open();
		}
		int attempts = 0;
		bool headerFound = false;
		List<int> buffer = new List<int>();

		while (stream.IsOpen && isReading)
		{
			// new reading method
			while (!headerFound && attempts < MAX_RETRY_ATTEMPTS)
			{
				if (!isReading)
				{
					stream.Close();
					return;
				}


				attempts++;
				Debug.Log($"Searching for the header {port}");

				buffer.Add(stream.BaseStream.ReadByte());
				if (buffer.Count > PACKET_SIZE)
				{
					buffer.RemoveAt(0);
				}
				if (buffer.Count == PACKET_SIZE)
				{
					if (buffer.ElementAt(0) == PACKET_HEADER_BYTE &&
						buffer.ElementAt(1) == PACKET_HEADER_BYTE &&
						buffer.ElementAt(PACKET_SIZE - 1) == PACKET_FOOTER_BYTE &&
						buffer.ElementAt(PACKET_SIZE - 2) == PACKET_FOOTER_BYTE)
					{
						headerFound = true;
						break;
					}
				}
			}
			if (!headerFound)
			{
				Debug.Log("No header");
				return;
			}
			try
			{
				byte[] packet = new byte[PACKET_SIZE];
				for (int i = 0; i < PACKET_SIZE; i++)
				{
					packet[i] = (byte)stream.BaseStream.ReadByte();
				}
				rawData = packet;
				decodeMethods[sensorType](port, packet);
			}
			catch (Exception ex) {
				Debug.LogWarning(ex.Message);
				stream.Close();
			}
		}
		stream.Close();

	}

	private void IdentifySerialDevice(string port) {
		Debug.Log("Identify");
		byte[] data = new byte[PACKET_SIZE];
		SerialPort serialPort = new SerialPort(port, BAUD_RATE);
		serialPort.ReadTimeout = READ_TIMEOUT;
		serialPort.Open();
		int attempts = 0;

		if (!serialPort.BaseStream.CanRead)
		{
			Debug.Log($"Can't read {port}");
		}

		List<int> buffer = new List<int>();
		bool headerFound = false;
        while (attempts < MAX_RETRY_ATTEMPTS)
        {
            attempts++;
            //Debug.Log($"Searching for the header {port}");
			
			buffer.Add(serialPort.BaseStream.ReadByte());
			if (buffer.Count > PACKET_SIZE) 
			{
				buffer.RemoveAt(0);
			}
			if (buffer.Count == PACKET_SIZE) {
				if (buffer.ElementAt(0) == PACKET_HEADER_BYTE && 
					buffer.ElementAt(1) == PACKET_HEADER_BYTE && 
					buffer.ElementAt(PACKET_SIZE-1) == PACKET_FOOTER_BYTE && 
					buffer.ElementAt(PACKET_SIZE-2) == PACKET_FOOTER_BYTE)
				{
					headerFound = true;
					break;
				}
			}
		}
		if (!headerFound) 
		{ 
			Debug.Log("No header"); 
			return; 
		}
		try
        {
			for (int i=0; i<PACKET_SIZE; i++) {
				data[i] = (byte)serialPort.BaseStream.ReadByte();
			}
		}
        catch (Exception e)
        {
            Debug.LogError(e.Message);
        }

        Debug.Log("Identify done");
		int KIWRIOUS_SENSOR_TYPE_INDEX = 2;
		if (Enum.IsDefined(typeof(SENSOR_TYPE), (SENSOR_TYPE)data[KIWRIOUS_SENSOR_TYPE_INDEX]))
		{
			string sensorName = Enum.GetName(typeof(SENSOR_TYPE), data[KIWRIOUS_SENSOR_TYPE_INDEX]);
			kiwriousSensorRegistry.Add(new KiwriousSensor(sensorName, data[KIWRIOUS_SENSOR_TYPE_INDEX], port));
			connectedKiwriousSensors.Add(new KiwriousSensor(sensorName, data[KIWRIOUS_SENSOR_TYPE_INDEX], port));
			Debug.Log($"{sensorName} sensor connected!");
		}
		else {
			Debug.Log($"type [{data?[KIWRIOUS_SENSOR_TYPE_INDEX]} is not registered as a kiwrious sensor]");
		};
        serialPort.Close();
	}

	void OnDisable() {
		//StopSerialReader();
		isReading = false;
	}

	public byte[] GetSensorRawData() {
		return rawData;
	}

#region Decode methods
	private void DecodeConductivity(string port, byte[] data)
	{
		var data0f = BitConverter.ToUInt16(data.Skip(6).Take(2).ToArray(), 0);
		var data1f = BitConverter.ToUInt16(data.Skip(8).Take(2).ToArray(), 0);
		var p = data0f * data1f;
		if (p == 0) { conductivity = 0; return; }
		conductivity = (float)(1f / p * Math.Pow(10, 6));
        if (conductivity > MAX_CONDUCTANCE_VALUE)
        {
            conductivity = MAX_CONDUCTANCE_VALUE;
        }
    }

    private void DecodeHumidity(string port, byte[] data)
	{
		temperature = BitConverter.ToInt16(data.Skip(6).Take(2).ToArray(), 0) / 100;
		humidity = BitConverter.ToInt16(data.Skip(8).Take(2).ToArray(), 0) / 100;
	}

	private void DecodeUV(string port, byte[] data)
	{
		lux = BitConverter.ToSingle(data.Skip(6).Take(4).ToArray(), 0);
		uv = BitConverter.ToSingle(data.Skip(10).Take(4).ToArray(), 0);
	}

	private void DecodeColor(string port, byte[] data)
	{
		byte r = (byte)Math.Sqrt(BitConverter.ToUInt16(data.Skip(6).Take(2).ToArray(), 0));
		byte g = (byte)Math.Sqrt(BitConverter.ToUInt16(data.Skip(8).Take(2).ToArray(), 0));
		byte b = (byte)Math.Sqrt(BitConverter.ToUInt16(data.Skip(10).Take(2).ToArray(), 0));
		//var w = BitConverter.ToUInt16(data.Skip(12).Take(2).ToArray(), 0);
		Color.RGBToHSV(new Color32(r, g, b, 255), out color_h, out color_s, out color_v);
		color_h *= 360;
		color_s *= 100;
		color_v *= 100;
	}

	private void DecodeCardio(string port, byte[] data) {
		uint data0 = BitConverter.ToUInt32(data.Skip(6).Take(4).ToArray(), 0);
		uint data1 = BitConverter.ToUInt32(data.Skip(10).Take(4).ToArray(), 0);
		uint data2 = BitConverter.ToUInt32(data.Skip(14).Take(4).ToArray(), 0);
		uint data3 = BitConverter.ToUInt32(data.Skip(18).Take(4).ToArray(), 0);
		heart_rate = 72; // for testing;
		// implement decode code here...
	}

	private void DecodeThermal(string port, byte[] data)
	{
		d_temperature = BitConverter.ToInt16(data.Skip(6).Take(2).ToArray(), 0) / 100;
		a_temperature = BitConverter.ToInt16(data.Skip(8).Take(2).ToArray(), 0) / 100;
	}

	private void DecodeThermal2(string port, byte[] data)
	{
		a_temperature = BitConverter.ToInt16(data.Skip(6).Take(2).ToArray(), 0) / 100;
		ushort x = BitConverter.ToUInt16(data.Skip(8).Take(2).ToArray(), 0);
		float a = BitConverter.ToSingle(data.Skip(10).Take(4).ToArray(), 0);
		float b = BitConverter.ToSingle(data.Skip(14).Take(4).ToArray(), 0);
		float c = BitConverter.ToSingle(data.Skip(18).Take(4).ToArray(), 0);
		d_temperature = (float)(Math.Round(((a * Math.Pow(x, 2)) / Math.Pow(10, 5) + b * x + c)));
	}

	private void DecodeVOC(string port, byte[] data)
	{
		voc1 = BitConverter.ToUInt16(data.Skip(6).Take(2).ToArray(), 0);
		voc2 = BitConverter.ToUInt16(data.Skip(8).Take(2).ToArray(), 0);
	}
#endregion

#region Helper methods
	private int GetSensorTypeByPort(string port)
	{
		KiwriousSensor sensor = kiwriousSensorRegistry.Where(s => s.Port == port).FirstOrDefault();
		if (sensor != null)
		{
			return sensor.Type;
		}
		return 0;
	}

	private bool IsKiwriousSensorConnectedAt(string port)
	{
		return kiwriousSensorRegistry.Any(s => s.Port == port) && connectedKiwriousSensors.Any(s => s.Port == port);
	}
#endregion

}


