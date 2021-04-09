import pika, os
import json
import requests

# Access the CLODUAMQP_URL environment variable and parse it (fallback to localhost)
url = os.environ.get('CLOUDAMQP_URL', 'amqps://avfepwdu:SS4fTAg36RK1hPQAUnyC6TH-4Mf3uyJo@fox.rmq.cloudamqp.com/avfepwdu')
params = pika.URLParameters(url)
connection = pika.BlockingConnection(params)
channel = connection.channel() # start a channel
channel.queue_declare(queue='CashboxBuyTicket', durable = True)
def callback(ch, method, properties, body):
  body_json = json.loads(body.decode("utf-8"))
  isAvailable = False
  print(body_json)
  if body_json.get('Ticket')==None:
      flightId = body_json.get('FlightId')
      #здесь будет get запрос для получение доступных рейсов
      #http_response = requests.get('')
      http_response = b'[{"FlightId":"1","HasVips":true}, {"FlightId":"2","HasVips":true}, {"FlightId":"3","HasVips":true}]' #просто для теста
      response_json = json.loads(http_response.decode("utf-8"))
      for i in range(len(response_json)):
          if (response_json[i].get('FlightId') == flightId):
              isAvailable = True
      if (isAvailable):
          body_json.update({'Ticket': True})
          print("Passenger", body_json.get('PassengerId'), "bought ticket")
      else:
          body_json.update({'IsBought': False})
          print("Passenger", body_json.get('PassengerId'), "tried to buy a ticket for unavailable flight")
  else:
      #добавить проверку на то, что пассажир не покупает билет на рейс, на который он уже купил билет
      body_json.update({'IsBought': False})
      print("Passenger", body_json.get('PassengerId'), "can't buy ticket")
  channel.basic_publish(exchange='',
                        routing_key='BuyPassengerQueue',
                        body=json.dumps(body_json))
  print("Sent", json.dumps(body_json), "\n")

channel.basic_consume('CashboxBuyTicket',
                      callback,
                      auto_ack=True)

print(' [*] Waiting for messages:')
channel.start_consuming()
connection.close()